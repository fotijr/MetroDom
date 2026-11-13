namespace MetroDom.Core.Bodies;

/// <summary>Why the brain didn't strike a Note On. Same cases as the firmware's <c>Refusal</c>.</summary>
public enum BrainRefusal
{
    /// <summary>It struck.</summary>
    None,

    /// <summary>Not one of its notes, or not its channel.</summary>
    NotMine,

    /// <summary>That actuator is still striking.</summary>
    Busy,

    /// <summary>Sooner than the note's <c>minIntervalMs</c> after its last strike.</summary>
    Interval,

    /// <summary>The actuator's thermal budget is spent.</summary>
    Duty,

    /// <summary><c>polyphony</c> actuators are already on.</summary>
    Polyphony,
}

/// <summary>What the brain did with one Note On.</summary>
/// <param name="Note">The note.</param>
/// <param name="Velocity">Its velocity, 1-127.</param>
/// <param name="Refusal"><see cref="BrainRefusal.None"/> when it struck.</param>
/// <param name="PulseMs">The strike's length; 0 when refused.</param>
/// <param name="Flash">The color its accent LEDs flashed (0xRRGGBB, after brightness and velocity), or null.</param>
public sealed record BrainStrike(int Note, int Velocity, BrainRefusal Refusal, int PulseMs, int? Flash)
{
    public bool Struck => Refusal == BrainRefusal.None;

    /// <summary>Why it was refused, in words, or null when it struck.</summary>
    public string? Why => Refusal switch
    {
        BrainRefusal.None => null,
        BrainRefusal.NotMine => "not one of its notes",
        BrainRefusal.Busy => "still striking",
        BrainRefusal.Interval => "repeat sooner than its minimum interval",
        BrainRefusal.Duty => "over its duty cycle",
        BrainRefusal.Polyphony => "too many at once",
        _ => Refusal.ToString(),
    };
}

/// <summary>
/// The brain firmware's strike decisions, in C#: what a brain running <paramref name="body"/>
/// strikes, how long, what it refuses and why, and which LEDs flash
/// (Instruments/metrodom_brain/src/brain/Brain.cpp, <c>noteOn</c>, <c>tick</c>, <c>pulseFor</c>,
/// <c>cool</c>, <c>flash</c>). Integer milliseconds, as the firmware's <c>millis()</c>.
/// The shared parity vectors (Instruments/metrodom_brain/test/vectors) run against both, so the
/// two can't drift apart. Not modeled: USB, output faults and Body Write, which only hardware has.
/// </summary>
/// <remarks>Not thread-safe; callers serialize.</remarks>
public sealed class BrainModel
{
    private readonly BodyDefinition _body;
    private readonly Actuator[] _actuators;
    private readonly State[] _state;
    private readonly int _maxDutyPercent;
    private readonly int _ledPercent;
    private readonly int _flashMs;

    public BrainModel(BodyDefinition body, long nowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;

        // The firmware's constructor clamps, so a body that slipped past validation still can't exceed the ceilings.
        var maxOnMs = Math.Clamp(body.MaxOnMs, 1, BodyImage.CeilingOnMs);
        _maxDutyPercent = Math.Clamp(body.MaxDutyPercent, 1, BodyImage.CeilingDutyPercent);
        var leds = body.Accent is { } strip ? Math.Min(strip.Leds, BodyImage.MaxLeds) : 0;
        _ledPercent = body.Accent is { } s ? Math.Min(s.MaxBrightnessPercent, BodyImage.CeilingLedPercent) : 0;
        _flashMs = Math.Clamp(body.Accent?.FlashMs ?? 80, 10, 1000);
        _actuators = [.. body.Notes.Take(BodyImage.MaxActuators).Select(a =>
        {
            var pulse = Math.Clamp(a.PulseMs, 1, maxOnMs);
            var minPulse = a.MinPulseMs >= pulse ? 0 : Math.Max(0, a.MinPulseMs);
            var led = a.Led is { } l && leds > 0 && _ledPercent > 0 && l.First < leds
                ? l with { Count = Math.Min(l.Count, leds - l.First) }
                : null;
            return new Actuator(a.Note & 0x7F, pulse, minPulse, Math.Max(a.MinIntervalMs, pulse), led is { Count: > 0 } ? led : null);
        })];
        _state = new State[_actuators.Length];
        Begin(nowMs);
    }

    /// <summary>Strikes so far.</summary>
    public int Struck { get; private set; }

    /// <summary>Refusals so far, by reason (<see cref="BrainRefusal.NotMine"/> included).</summary>
    public IReadOnlyDictionary<BrainRefusal, int> Refused => _refused;

    private readonly Dictionary<BrainRefusal, int> _refused = [];

    /// <summary>Everything off and every budget full, as at boot.</summary>
    public void Begin(long nowMs)
    {
        for (var i = 0; i < _state.Length; i++)
        {
            _state[i] = new State { HeatAt = nowMs };
        }
    }

    /// <summary>
    /// A Note On at <paramref name="nowMs"/>: ends pulses that are due (the firmware's loop ticks
    /// before it reads MIDI), then strikes or refuses. Velocity 0 is a Note Off: null, nothing happens.
    /// </summary>
    public BrainStrike? NoteOn(int channel, int note, int velocity, long nowMs)
    {
        if (velocity <= 0)
        {
            return null;
        }

        Tick(nowMs);
        note &= 0x7F;
        var index = Array.FindIndex(_actuators, a => a.Note == note);
        if ((_body.Channel is { } own && channel != own) || index < 0)
        {
            return Refuse(note, velocity, BrainRefusal.NotMine);
        }

        ref var s = ref _state[index];
        var a = _actuators[index];
        if (s.On || (s.HasStruck && nowMs - s.LastStrike < a.MinIntervalMs))
        {
            return Refuse(note, velocity, s.On ? BrainRefusal.Busy : BrainRefusal.Interval);
        }

        // Thermal budget: a leaky bucket that fills by each pulse and drains at maxDutyPercent.
        Cool(ref s, nowMs);
        var pulse = PulseFor(a, velocity);
        long capacity = (long)_body.DutyWindowMs * _maxDutyPercent;
        long cost = (long)pulse * 100;
        if (s.Heat + cost > capacity)
        {
            return Refuse(note, velocity, BrainRefusal.Duty);
        }

        if (_state.Count(x => x.On) >= Math.Max(1, _body.Polyphony))
        {
            return Refuse(note, velocity, BrainRefusal.Polyphony);
        }

        s.Heat += cost;
        s.On = true;
        s.HasStruck = true;
        s.Pulse = pulse;
        s.OnAt = nowMs;
        s.LastStrike = nowMs;
        Struck++;
        int? flash = null;
        if (a.Led is { } led)
        {
            flash = FlashColor(led.Color, velocity);
            s.Lit = true;
            s.LitAt = nowMs;
        }

        return new BrainStrike(note, Math.Min(velocity, 127), BrainRefusal.None, pulse, flash);
    }

    /// <summary>Ends pulses and flashes that are due, as the firmware's loop does.</summary>
    public void Tick(long nowMs)
    {
        for (var i = 0; i < _state.Length; i++)
        {
            ref var s = ref _state[i];
            if (s.On && nowMs - s.OnAt >= s.Pulse)
            {
                s.On = false;
            }

            if (s.Lit && nowMs - s.LitAt >= _flashMs)
            {
                s.Lit = false;
            }
        }
    }

    /// <summary>Panic: every actuator and LED off now. Intervals and budgets carry on, as in the firmware.</summary>
    public void AllOff()
    {
        for (var i = 0; i < _state.Length; i++)
        {
            _state[i].On = false;
            _state[i].Lit = false;
        }
    }

    /// <summary>Whether <paramref name="note"/>'s actuator is inside its pulse (as of the last call).</summary>
    public bool IsOn(int note) => Array.FindIndex(_actuators, a => a.Note == note) is var i and >= 0 && _state[i].On;

    /// <summary>Whether <paramref name="note"/>'s LEDs are lit (as of the last call).</summary>
    public bool IsLit(int note) => Array.FindIndex(_actuators, a => a.Note == note) is var i and >= 0 && _state[i].Lit;

    /// <summary>The strike length for <paramref name="note"/> at <paramref name="velocity"/>, or 0 if it isn't one of its notes.</summary>
    public int PulseFor(int note, int velocity) =>
        Array.Find(_actuators, a => a.Note == note) is { } a ? PulseFor(a, velocity) : 0;

    private static int PulseFor(Actuator a, int velocity)
    {
        if (a.MinPulseMs == 0)
        {
            return a.PulseMs;
        }

        var v = Math.Clamp(velocity, 1, 127);
        return a.MinPulseMs + ((a.PulseMs - a.MinPulseMs) * (v - 1) / 126);
    }

    /// <summary>The firmware's <c>flash</c>: color × brightness × velocity, dim but never dark.</summary>
    private int FlashColor(int color, int velocity)
    {
        var result = 0;
        for (var shift = 16; shift >= 0; shift -= 8)
        {
            var c = (color >> shift) & 0xFF;
            var v = c * _ledPercent * Math.Min(velocity, 127) / (100 * 127);
            result |= (v == 0 && c > 0 ? 1 : v) << shift;
        }

        return result;
    }

    private void Cool(ref State s, long nowMs)
    {
        var drained = (nowMs - s.HeatAt) * _maxDutyPercent;
        s.Heat = drained >= s.Heat ? 0 : s.Heat - drained;
        s.HeatAt = nowMs;
    }

    private BrainStrike Refuse(int note, int velocity, BrainRefusal why)
    {
        _refused[why] = _refused.GetValueOrDefault(why) + 1;
        return new BrainStrike(note, Math.Min(velocity, 127), why, 0, null);
    }

    private sealed record Actuator(int Note, int PulseMs, int MinPulseMs, int MinIntervalMs, AccentLed? Led);

    private struct State
    {
        public bool On;
        public bool HasStruck;
        public bool Lit;
        public long LitAt;
        public int Pulse;
        public long OnAt;
        public long LastStrike;
        public long Heat;
        public long HeatAt;
    }
}
