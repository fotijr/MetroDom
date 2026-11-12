namespace MetroDom.Core;

/// <summary>
/// What the Conductor knows about one instrument: the subset of the
/// <a href="../../docs/references/instrument-profile.md">instrument profile</a> that scheduling
/// and simulation use today.
/// </summary>
/// <param name="Name">Human-readable name.</param>
/// <param name="Latency">Command → audible onset for notes without their own entry in <see cref="NoteLatency"/>.</param>
/// <summary>
/// The thermal limits an instrument's firmware enforces per actuator: a leaky bucket holding
/// <see cref="MaxDutyPercent"/> of <see cref="DutyWindow"/> of on-time, each strike adding its
/// pulse, draining at <see cref="MaxDutyPercent"/>. See Instruments/metrodom_brain/README.md.
/// </summary>
/// <param name="MaxOnTime">Longest any single pulse may be (<c>maxOnMs</c>).</param>
/// <param name="MaxDutyPercent">Share of time an actuator may be on, sustained (1-100).</param>
/// <param name="DutyWindow">Size of the burst budget (<c>dutyWindowMs</c>, default 1 s).</param>
public sealed record ActuatorSafety(TimeSpan MaxOnTime, int MaxDutyPercent, TimeSpan DutyWindow)
{
    /// <summary>The firmware's default burst window.</summary>
    public static readonly TimeSpan DefaultDutyWindow = TimeSpan.FromSeconds(1);
}

public sealed record InstrumentProfile(string Name, TimeSpan Latency)
{
    /// <summary>Measured latency per note, overriding <see cref="Latency"/>.</summary>
    public IReadOnlyDictionary<int, TimeSpan> NoteLatency { get; init; } = new Dictionary<int, TimeSpan>();

    /// <summary>The only notes the instrument can play. <c>null</c> means any note.</summary>
    public IReadOnlySet<int>? Notes { get; init; }

    /// <summary>The channel (0-15) the instrument listens on by default, from <c>midi.channel</c>. <c>null</c> means it didn't say.</summary>
    public int? Channel { get; init; }

    /// <summary>
    /// Fastest repeat of the same note, for notes without their own entry in <see cref="NoteInterval"/>.
    /// Faster repeats are refused by the firmware.
    /// </summary>
    public TimeSpan MinInterval { get; init; }

    /// <summary>Fastest repeat per note (a note's own <c>minIntervalMs</c>), overriding <see cref="MinInterval"/>.</summary>
    public IReadOnlyDictionary<int, TimeSpan> NoteInterval { get; init; } = new Dictionary<int, TimeSpan>();

    /// <summary>
    /// Most actuators on at once (<c>polyphony</c>), a power-supply or actuator limit the firmware
    /// enforces. <c>null</c> means no limit.
    /// </summary>
    public int? Polyphony { get; init; }

    /// <summary>
    /// The firmware's thermal limits (<c>safety</c>). <c>null</c> means the instrument declared none
    /// (a software instrument has no coil to protect).
    /// </summary>
    public ActuatorSafety? Safety { get; init; }

    /// <summary>How long each note's actuator stays on per strike (<c>pulseMs</c>), where declared.</summary>
    public IReadOnlyDictionary<int, TimeSpan> NotePulse { get; init; } = new Dictionary<int, TimeSpan>();

    /// <summary>
    /// The instrument answered the Identity Request as MetroDom, so MetroDom SysEx (the all-off)
    /// is safe to send it. Set by discovery, never read from the JSON: anyone can write a profile.
    /// </summary>
    public bool SpeaksMetroDom { get; init; }

    /// <summary>
    /// How long <paramref name="note"/>'s actuator is on per strike: its <c>pulseMs</c>, else
    /// <c>safety.maxOnMs</c> (the longest the firmware allows, so a guess never under-counts heat),
    /// else zero.
    /// </summary>
    public TimeSpan PulseFor(int note) =>
        NotePulse.TryGetValue(note, out var pulse) ? pulse : Safety?.MaxOnTime ?? TimeSpan.Zero;

    /// <summary>How soon <paramref name="note"/> may strike again: its own interval, else <see cref="MinInterval"/>.</summary>
    public TimeSpan IntervalFor(int note) => NoteInterval.TryGetValue(note, out var interval) ? interval : MinInterval;

    /// <summary>Latency for <paramref name="note"/>.</summary>
    public TimeSpan LatencyFor(int note) => NoteLatency.TryGetValue(note, out var latency) ? latency : Latency;

    /// <summary>The slowest note: how far ahead the scheduler must look for this instrument.</summary>
    public TimeSpan MaxLatency => NoteLatency.Values.Append(Latency).Max();

    /// <summary>Whether the instrument has an actuator for <paramref name="note"/>.</summary>
    public bool CanPlay(int note) => Notes is null || Notes.Contains(note);
}
