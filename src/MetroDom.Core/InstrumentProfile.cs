namespace MetroDom.Core;

/// <summary>
/// What the Conductor knows about one instrument: the subset of the
/// <a href="../../docs/references/instrument-profile.md">instrument profile</a> that scheduling
/// and simulation use today.
/// </summary>
/// <param name="Name">Human-readable name.</param>
/// <param name="Latency">Command → audible onset for notes without their own entry in <see cref="NoteLatency"/>.</param>
public sealed record InstrumentProfile(string Name, TimeSpan Latency)
{
    /// <summary>Measured latency per note, overriding <see cref="Latency"/>.</summary>
    public IReadOnlyDictionary<int, TimeSpan> NoteLatency { get; init; } = new Dictionary<int, TimeSpan>();

    /// <summary>The only notes the instrument can play. <c>null</c> means any note.</summary>
    public IReadOnlySet<int>? Notes { get; init; }

    /// <summary>The channel (0-15) the instrument listens on by default, from <c>midi.channel</c>. <c>null</c> means it didn't say.</summary>
    public int? Channel { get; init; }

    /// <summary>Fastest repeat of the same note. Faster repeats are refused by the firmware.</summary>
    public TimeSpan MinInterval { get; init; }

    /// <summary>Latency for <paramref name="note"/>.</summary>
    public TimeSpan LatencyFor(int note) => NoteLatency.TryGetValue(note, out var latency) ? latency : Latency;

    /// <summary>The slowest note: how far ahead the scheduler must look for this instrument.</summary>
    public TimeSpan MaxLatency => NoteLatency.Values.Append(Latency).Max();

    /// <summary>Whether the instrument has an actuator for <paramref name="note"/>.</summary>
    public bool CanPlay(int note) => Notes is null || Notes.Contains(note);
}
