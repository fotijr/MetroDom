using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Safety;

/// <summary>
/// Limits from the active policy (ADR 0005) that apply on top of what each instrument declares.
/// <c>null</c> means no limit.
/// </summary>
public sealed record SafetyLimits
{
    /// <summary>No policy limits: only the instrument's own notes and minimum interval apply.</summary>
    public static readonly SafetyLimits None = new();

    /// <summary>Highest velocity sent (1-127). Louder notes are turned down, not dropped.</summary>
    public int? MaxVelocity { get; init; }

    /// <summary>Most strikes one instrument may play in any one-second window.</summary>
    public int? MaxNotesPerSecond { get; init; }

    /// <summary>A daily window with its own velocity cap, or silence.</summary>
    public QuietHours? Quiet { get; init; }

    /// <summary>
    /// These limits with a night's overrides on top (the command line over the policy): a lower
    /// velocity or rate wins, so an override can only tighten them; quiet hours, if given,
    /// replace the window.
    /// </summary>
    public SafetyLimits Overridden(int? maxVelocity, int? maxNotesPerSecond, QuietHours? quiet) => new()
    {
        MaxVelocity = Lower(MaxVelocity, maxVelocity),
        MaxNotesPerSecond = Lower(MaxNotesPerSecond, maxNotesPerSecond),
        Quiet = quiet ?? Quiet,
    };

    /// <summary>"velocity ≤ 100 · ≤ 16 notes/s · quiet 22:00-08:00 silent", or "none".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (MaxVelocity is { } velocity)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"velocity ≤ {velocity}"));
        }

        if (MaxNotesPerSecond is { } rate)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"≤ {rate} notes/s per instrument"));
        }

        if (Quiet is { } quiet)
        {
            parts.Add($"quiet {quiet}");
        }

        return parts.Count == 0 ? "none" : string.Join(" · ", parts);
    }

    private static int? Lower(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}

/// <summary>
/// Local time-of-day window, e.g. 23:00-07:00 (may cross midnight). Notes inside it are capped
/// at <see cref="MaxVelocity"/>; 0 means no new notes at all.
/// </summary>
public sealed record QuietHours(TimeOnly Start, TimeOnly End, int MaxVelocity = 0)
{
    /// <summary>Whether <paramref name="time"/> falls inside the window (start inclusive, end exclusive).</summary>
    public bool Contains(TimeOnly time) => Start <= End
        ? time >= Start && time < End
        : time >= Start || time < End;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Start:HH\\:mm}-{End:HH\\:mm}{(MaxVelocity == 0 ? " silent" : $" ≤ velocity {MaxVelocity}")}");
}

/// <summary>Why the governor didn't pass a note.</summary>
public enum BlockReason
{
    /// <summary>The instrument has no actuator for the note.</summary>
    NotPlayable,

    /// <summary>Same note again sooner than the instrument's minimum interval.</summary>
    TooSoon,

    /// <summary>Over the policy's notes-per-second limit for this instrument.</summary>
    RateLimited,

    /// <summary>Inside silent quiet hours.</summary>
    QuietHours,

    /// <summary>The actuator's thermal budget is spent (the profile's <c>safety</c> duty cycle).</summary>
    DutyCycle,

    /// <summary>As many actuators as the profile's <c>polyphony</c> are already on.</summary>
    Polyphony,
}

/// <summary>A note the governor held back.</summary>
public sealed record BlockedNote(string Instrument, int Note, int Channel, BlockReason Reason);

/// <summary>
/// The Conductor's safety layer for one instrument (docs/RELIABILITY.md, layer 2). Wraps the
/// instrument's output and checks every new note against the instrument's profile and the
/// policy's limits before it leaves the Conductor.
/// </summary>
/// <remarks>
/// From the profile: playable notes, minimum interval (and no repeat while the actuator is still
/// striking), the duty cycle and polyphony. The last two mirror the brain firmware's own checks
/// (Instruments/metrodom_brain): a per-actuator leaky bucket holding <c>maxDutyPercent</c> of
/// <c>dutyWindowMs</c> of on-time, filled by each strike's pulse and draining at
/// <c>maxDutyPercent</c>; and at most <c>polyphony</c> actuators inside their pulse at once.
/// </remarks>
/// <remarks>
/// Only new notes (Note On with velocity &gt; 0) are ever held back or changed. Anything that
/// makes things quieter or stops them always passes: Note Off, All Sound Off / All Notes Off
/// (panic), and every other non-note message. A governor can't leave an actuator on.
/// The firmware still enforces its own limits and never trusts the host; this layer exists so
/// the Conductor doesn't send what would be refused, and so policy (crowd, curfew) is applied
/// in one place for songs and live input alike.
/// Events are never modified in place: the scheduler sends the same event object to several
/// instruments, so a velocity cap sends a copy.
/// </remarks>
public sealed class SafetyGovernor : IMidiOutput
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly IClock _clock;
    private readonly Func<TimeOnly> _timeOfDay;
    private readonly Dictionary<int, Actuator> _actuators = [];
    private readonly Queue<TimeSpan> _recent = new();
    private readonly long[] _blocked = new long[Enum.GetValues<BlockReason>().Length];
    private long _passed;
    private long _capped;

    /// <param name="inner">The instrument's real output.</param>
    /// <param name="profile">What the instrument declares: notes and minimum interval.</param>
    /// <param name="limits">Policy limits. <see cref="SafetyLimits.None"/> for profile rules only.</param>
    /// <param name="clock">Monotonic clock for intervals and rates.</param>
    /// <param name="timeOfDay">Local wall-clock time for quiet hours. Defaults to the system's.</param>
    public SafetyGovernor(IMidiOutput inner, InstrumentProfile profile, SafetyLimits limits, IClock clock, Func<TimeOnly>? timeOfDay = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);
        if (limits.MaxVelocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxVelocity must be 1-127.");
        }

        if (limits.MaxNotesPerSecond is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "MaxNotesPerSecond must be at least 1.");
        }

        if (limits.Quiet is { MaxVelocity: < 0 or > 127 })
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Quiet hours velocity must be 0-127.");
        }

        Inner = inner;
        Profile = profile;
        Limits = limits;
        _clock = clock;
        _timeOfDay = timeOfDay ?? (() => TimeOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>Raised for each note held back. Called on the sending thread: keep it short.</summary>
    public event Action<BlockedNote>? Blocked;

    /// <summary>The instrument's real output.</summary>
    public IMidiOutput Inner { get; }

    public InstrumentProfile Profile { get; }

    public SafetyLimits Limits { get; }

    public string Name => Inner.Name;

    /// <summary>New notes passed on, capped or not.</summary>
    public long Passed => Interlocked.Read(ref _passed);

    /// <summary>New notes passed on with a lower velocity.</summary>
    public long Capped => Interlocked.Read(ref _capped);

    /// <summary>All new notes held back, for any reason.</summary>
    public long TotalBlocked => _blocked.Select((_, i) => Interlocked.Read(ref _blocked[i])).Sum();

    /// <summary>New notes held back for <paramref name="reason"/>.</summary>
    public long BlockedFor(BlockReason reason) => Interlocked.Read(ref _blocked[(int)reason]);

    public void Send(MidiEvent midiEvent)
    {
        ArgumentNullException.ThrowIfNull(midiEvent);
        if (midiEvent is not NoteOnEvent noteOn || noteOn.Velocity == 0)
        {
            Inner.Send(midiEvent);
            return;
        }

        var toSend = Check(noteOn, out var reason);
        if (toSend is null)
        {
            Interlocked.Increment(ref _blocked[(int)reason]);
            Blocked?.Invoke(new BlockedNote(Name, noteOn.NoteNumber, noteOn.Channel, reason));
            return;
        }

        Interlocked.Increment(ref _passed);
        if (!ReferenceEquals(toSend, noteOn))
        {
            Interlocked.Increment(ref _capped);
        }

        Inner.Send(toSend);
    }

    /// <summary>A one-line summary of what was held back, or null if nothing was.</summary>
    public string? Summary()
    {
        if (TotalBlocked == 0 && Capped == 0)
        {
            return null;
        }

        var parts = Enum.GetValues<BlockReason>()
            .Where(r => BlockedFor(r) > 0)
            .Select(r => string.Create(CultureInfo.InvariantCulture, $"{BlockedFor(r)} {Describe(r)}"))
            .ToList();
        if (Capped > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Capped} turned down"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>Short human wording for a reason.</summary>
    public static string Describe(BlockReason reason) => reason switch
    {
        BlockReason.NotPlayable => "not playable",
        BlockReason.TooSoon => "too soon",
        BlockReason.RateLimited => "over rate limit",
        BlockReason.QuietHours => "in quiet hours",
        BlockReason.DutyCycle => "over duty cycle",
        BlockReason.Polyphony => "too many at once",
        _ => reason.ToString(),
    };

    /// <summary>The event to send (the original, or a quieter copy), or null to hold it back.</summary>
    private NoteOnEvent? Check(NoteOnEvent noteOn, out BlockReason reason)
    {
        reason = default;
        int note = noteOn.NoteNumber;
        if (!Profile.CanPlay(note))
        {
            reason = BlockReason.NotPlayable;
            return null;
        }

        var cap = Limits.MaxVelocity ?? 127;
        if (Limits.Quiet is { } quiet && quiet.Contains(_timeOfDay()))
        {
            if (quiet.MaxVelocity == 0)
            {
                reason = BlockReason.QuietHours;
                return null;
            }

            cap = Math.Min(cap, quiet.MaxVelocity);
        }

        lock (_gate)
        {
            var now = _clock.Now;
            var pulse = Profile.PulseFor(note);
            var interval = Profile.IntervalFor(note); // per note, as the brain enforces it
            var struck = _actuators.TryGetValue(note, out var actuator);
            if (struck && (now < actuator.OnUntil || (interval > TimeSpan.Zero && now - actuator.LastStrike < interval)))
            {
                reason = BlockReason.TooSoon;
                return null;
            }

            var heat = 0.0;
            if (Profile.Safety is { } safety)
            {
                // Units as in the firmware: milliseconds x percent.
                heat = struck ? Math.Max(0, actuator.Heat - ((now - actuator.HeatAt).TotalMilliseconds * safety.MaxDutyPercent)) : 0;
                var capacity = safety.DutyWindow.TotalMilliseconds * safety.MaxDutyPercent;
                var cost = pulse.TotalMilliseconds * 100;
                if (heat + cost > capacity)
                {
                    reason = BlockReason.DutyCycle;
                    return null;
                }

                heat += cost;
            }

            if (Profile.Polyphony is { } polyphony && _actuators.Values.Count(x => now < x.OnUntil) >= polyphony)
            {
                reason = BlockReason.Polyphony;
                return null;
            }

            if (Limits.MaxNotesPerSecond is { } maxRate)
            {
                while (_recent.Count > 0 && now - _recent.Peek() >= RateWindow)
                {
                    _recent.Dequeue();
                }

                if (_recent.Count >= maxRate)
                {
                    reason = BlockReason.RateLimited;
                    return null;
                }

                _recent.Enqueue(now);
            }

            _actuators[note] = new Actuator(now, now + pulse, heat, now);
        }

        return noteOn.Velocity <= cap
            ? noteOn
            : new NoteOnEvent(noteOn.NoteNumber, (SevenBitNumber)cap) { Channel = noteOn.Channel, DeltaTime = noteOn.DeltaTime };
    }

    /// <summary>One note's actuator: when it last struck, when that pulse ends, and its heat (ms x percent) as of <see cref="HeatAt"/>.</summary>
    private readonly record struct Actuator(TimeSpan LastStrike, TimeSpan OnUntil, double Heat, TimeSpan HeatAt);
}
