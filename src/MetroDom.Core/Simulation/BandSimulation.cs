using MetroDom.Core.Bodies;
using MetroDom.Core.Safety;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Simulation;

/// <summary>A virtual instrument in the simulated band and the channels it plays.</summary>
public sealed record SimulatedPart(InstrumentProfile Profile, IReadOnlyList<int> Channels)
{
    /// <summary>
    /// A body file: the part then plays as a brain running it would (<see cref="BrainModel"/>),
    /// and <see cref="Profile"/> should be its <see cref="BodyDefinition.ToProfile"/>.
    /// </summary>
    public BodyDefinition? Body { get; init; }
}

/// <summary>How to run a simulation.</summary>
public sealed record SimulationOptions
{
    /// <summary>Send early by each instrument's latency (ADR 0004). False shows the uncorrected band.</summary>
    public bool Compensate { get; init; } = true;

    /// <summary>Each strike lands up to ± this much off its declared latency.</summary>
    public TimeSpan Jitter { get; init; }

    /// <summary>Seed for the jitter. Same seed, same result.</summary>
    public int Seed { get; init; }

    /// <summary>
    /// Put the safety governor in front of each instrument, as <c>play</c> does. False sends
    /// straight to the instruments, as a DAW would, so a body's brain refuses what it must.
    /// </summary>
    public bool Governed { get; init; } = true;
}

/// <summary>One note as heard. <paramref name="Offset"/> is how late (+) or early (−) it sounded.</summary>
public sealed record SimulatedNote(string Instrument, int Note, TimeSpan SongTime, TimeSpan Offset);

/// <summary>Per-instrument totals.</summary>
/// <param name="Held">Notes the safety governor held back, as <c>play</c> would.</param>
/// <param name="HeldWhy">The governor's summary (counts by reason, notes turned down), or null.</param>
/// <param name="Dropped">Notes that reached the instrument and it has no actuator for.</param>
/// <param name="Refused">Notes that reached the instrument and its firmware refused. With the
/// governor in front this should stay 0: anything here means the two disagree.</param>
public sealed record InstrumentResult(
    string Name,
    TimeSpan Latency,
    int Played,
    int Held,
    string? HeldWhy,
    int Dropped,
    int Refused,
    TimeSpan MinOffset,
    TimeSpan MaxOffset,
    bool GotPanic)
{
    /// <summary>For a body part: what its brain refused, by reason. Empty otherwise.</summary>
    public IReadOnlyDictionary<BrainRefusal, int> RefusedBy { get; init; } = new Dictionary<BrainRefusal, int>();
}

/// <summary>What the simulated band did.</summary>
/// <param name="Lookahead">How far the band ran behind the song clock.</param>
/// <param name="Instruments">Per-instrument totals, in part order.</param>
/// <param name="Notes">Every strike, in the order heard.</param>
/// <param name="SharedMoments">Song moments where two or more instruments play together.</param>
/// <param name="MaxSpread">Worst gap between instruments that should have sounded together.</param>
public sealed record SimulationResult(
    TimeSpan Lookahead,
    IReadOnlyList<InstrumentResult> Instruments,
    IReadOnlyList<SimulatedNote> Notes,
    int SharedMoments,
    TimeSpan MaxSpread);

/// <summary>
/// Plays a song to a band of <see cref="VirtualInstrument"/>s on a <see cref="VirtualClock"/>,
/// through the real <see cref="Orchestra"/>, <see cref="LatencyScheduler"/> and
/// <see cref="SafetyGovernor"/> (profile rules, as <c>play</c> applies them), then panics.
/// Runs instantly and gives the same answer every time: the cheap way to prove scheduling ideas
/// before hardware exists (roadmap S1).
/// </summary>
public static class BandSimulation
{
    public static SimulationResult Run(
        IReadOnlyList<TimedMidiEvent> song,
        IReadOnlyList<SimulatedPart> parts,
        SimulationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(parts);
        options ??= new SimulationOptions();

        var clock = new VirtualClock();
        var orchestra = new Orchestra();
        var instruments = parts
            .Select((part, i) => part.Body is { } body
                ? VirtualInstrument.ForBody(body, clock, options.Jitter, options.Seed + i)
                : new VirtualInstrument(part.Profile, clock, options.Jitter, options.Seed + i))
            .ToArray();
        var governors = instruments
            .Select(i => options.Governed ? new SafetyGovernor(i, i.Profile, SafetyLimits.None, clock) : null)
            .ToArray();
        var outputs = instruments.Select((i, n) => (IMidiOutput?)governors[n] ?? i).ToArray();
        for (var i = 0; i < parts.Count; i++)
        {
            foreach (var channel in parts[i].Channels)
            {
                orchestra.Assign(channel, outputs[i]);
            }
        }

        var scheduler = new LatencyScheduler(
            orchestra,
            clock,
            outputs.Select((o, n) => (o, instruments[n].Profile)).ToDictionary(x => x.o, x => x.Profile),
            options.Compensate);

        var notes = new List<SimulatedNote>();
        foreach (var instrument in instruments)
        {
            instrument.Struck += (source, strike) =>
            {
                var songTime = scheduler.Current!.SongTime;
                notes.Add(new SimulatedNote(source.Name, strike.Note, songTime, strike.SoundsAt - scheduler.SoundTime(songTime)));
            };
        }

        scheduler.Start(song);
        while (scheduler.NextDue is { } due)
        {
            clock.AdvanceTo(due);
            scheduler.Pump();
        }

        scheduler.Stop();

        var shared = notes
            .GroupBy(n => n.SongTime)
            .Where(g => g.Select(n => n.Instrument).Distinct().Count() > 1)
            .Select(g => g.Max(n => n.Offset) - g.Min(n => n.Offset))
            .ToArray();

        return new SimulationResult(
            scheduler.Lookahead,
            instruments.Select((i, n) => Summarize(i, governors[n], notes)).ToArray(),
            notes.OrderBy(n => n.SongTime + n.Offset).ToArray(),
            shared.Length,
            shared.DefaultIfEmpty().Max());
    }

    private static InstrumentResult Summarize(VirtualInstrument instrument, SafetyGovernor? governor, List<SimulatedNote> notes)
    {
        var offsets = notes.Where(n => n.Instrument == instrument.Name).Select(n => n.Offset).DefaultIfEmpty().ToArray();
        return new InstrumentResult(
            instrument.Name,
            instrument.Profile.Latency,
            instrument.Strikes.Count,
            (int)(governor?.TotalBlocked ?? 0),
            governor?.Summary(),
            instrument.Dropped,
            instrument.Refused,
            offsets.Min(),
            offsets.Max(),
            instrument.PanicMessages > 0)
        {
            RefusedBy = instrument.RefusedBy,
        };
    }
}
