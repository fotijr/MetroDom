namespace MetroDom.Core.Calibration;

/// <summary>Outcome of the boot check for one instrument.</summary>
public enum CheckStatus
{
    /// <summary>Every note heard, each within the drift threshold of its stored latency.</summary>
    Ok,

    /// <summary>At least one note is off by more than the threshold: recalibrate.</summary>
    Drifted,

    /// <summary>Some notes heard, some never: an actuator or the onset input may be failing.</summary>
    Partial,

    /// <summary>Nothing heard at all: unpowered, unplugged, muted, or the onset input can't hear it.</summary>
    Silent,
}

/// <summary>One note's stored and measured latency.</summary>
public sealed record NoteDrift(int Note, TimeSpan Stored, TimeSpan Measured)
{
    public TimeSpan Delta => Measured - Stored;
}

/// <summary>The boot check's verdict on one instrument (ADR 0004: flag drift, don't silently change anything).</summary>
public sealed record LatencyCheck(CheckStatus Status, IReadOnlyList<NoteDrift> Notes, IReadOnlyList<int> Silent, CalibrationResult Measured)
{
    /// <summary>The note furthest from its stored latency, if any was heard.</summary>
    public NoteDrift? Worst => Notes.MaxBy(n => n.Delta.Duration());

    /// <summary>Compares what was heard with the latencies the band is playing with.</summary>
    public static LatencyCheck Compare(CalibrationResult measured, InstrumentProfile stored, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(stored);
        var notes = measured.Notes.Where(n => n.Heard).Select(n => new NoteDrift(n.Note, stored.LatencyFor(n.Note), n.Median!.Value)).ToArray();
        var silent = measured.Notes.Where(n => !n.Heard).Select(n => n.Note).ToArray();
        var status = notes.Length == 0 ? CheckStatus.Silent
            : silent.Length > 0 ? CheckStatus.Partial
            : notes.Any(n => n.Delta.Duration() > threshold) ? CheckStatus.Drifted
            : CheckStatus.Ok;
        return new LatencyCheck(status, notes, silent, measured);
    }
}
