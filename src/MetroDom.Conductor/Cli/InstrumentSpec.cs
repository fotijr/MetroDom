using System.Globalization;
using MetroDom.Core;

namespace MetroDom.Conductor.Cli;

/// <summary>
/// A virtual instrument written on the command line as <c>NAME[=CHANNELS]@LATENCY[/NOTES]</c>:
/// <c>drums=10@35/36,38,76</c> is "drums" on channel 10 with 35 ms latency that can play kick,
/// snare and woodblock; <c>xylo@10</c> plays every channel and every note. Channels use musician
/// numbering (1-16) and may be ranges (<c>1-9,11-16</c>); notes are numbers or names (<c>C4-C6</c>).
/// <c>NAME[=CHANNELS]@PROFILE.json</c> takes everything but the name (and channels, if given)
/// from an instrument's profile file: <c>xylo@Instruments/xylophone/profile.json</c>.
/// </summary>
internal sealed record InstrumentSpec(string Name, IReadOnlyList<int> Channels, TimeSpan Latency, IReadOnlySet<int>? Notes = null)
{
    /// <summary>The profile file's contents, when the spec named one.</summary>
    public InstrumentProfile? Profile { get; init; }

    /// <summary>All 16 channels, zero-based.</summary>
    public static readonly IReadOnlyList<int> AllChannels = Enumerable.Range(0, Orchestra.ChannelCount).ToArray();

    /// <summary>The roadmap S1 band: melody at 10 ms on every channel but 10, drums at 35 ms on 10.</summary>
    public static readonly IReadOnlyList<InstrumentSpec> DefaultBand =
    [
        new("xylo", AllChannels.Where(c => c != 9).ToArray(), TimeSpan.FromMilliseconds(10)),
        new("drums", [9], TimeSpan.FromMilliseconds(35)),
    ];

    public InstrumentProfile ToProfile() =>
        Profile is { } file ? file with { Name = Name, Channel = Channels[0] } : new(Name, Latency) { Notes = Notes, Channel = Channels[0] };

    /// <summary>Parses every spec, or returns the first error.</summary>
    public static bool TryParseAll(IEnumerable<string>? raw, out IReadOnlyList<InstrumentSpec> specs, out string? error)
    {
        var parsed = new List<InstrumentSpec>();
        specs = parsed;
        error = null;
        foreach (var text in raw ?? [])
        {
            if (!TryParse(text, out var spec, out error))
            {
                return false;
            }

            if (parsed.Any(p => string.Equals(p.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"instrument '{spec.Name}' is listed twice.";
                return false;
            }

            parsed.Add(spec);
        }

        return true;
    }

    public static bool TryParse(string raw, out InstrumentSpec spec, out string? error)
    {
        spec = null!;
        error = $"'{raw}' is not NAME[=CHANNELS]@LATENCY_MS[/NOTES] or NAME[=CHANNELS]@PROFILE.json (e.g. drums=10@35/36,38).";
        var firstAt = raw.IndexOf('@', StringComparison.Ordinal);
        if (firstAt > 0 && ProfileFile.Names(raw[(firstAt + 1)..]))
        {
            return TryParseWithProfile(raw, raw[..firstAt], raw[(firstAt + 1)..], out spec, out error);
        }

        IReadOnlySet<int>? notes = null;
        var slash = raw.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            if (!NoteList.TryParse(raw[(slash + 1)..], out var parsedNotes, out var noteError))
            {
                error = $"'{raw}': {noteError}";
                return false;
            }

            notes = parsedNotes;
            raw = raw[..slash];
        }

        var at = raw.LastIndexOf('@');
        if (at <= 0)
        {
            return false;
        }

        var latencyText = raw[(at + 1)..].Trim();
        if (latencyText.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            latencyText = latencyText[..^2];
        }

        if (!double.TryParse(latencyText, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms is < 0 or > 10_000)
        {
            return false;
        }

        var head = raw[..at];
        var eq = head.IndexOf('=', StringComparison.Ordinal);
        var name = (eq < 0 ? head : head[..eq]).Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            return false;
        }

        IReadOnlyList<int> channels = AllChannels;
        if (eq >= 0 && !ChannelList.TryParse(head[(eq + 1)..], out channels, out var channelError))
        {
            error = $"'{raw}': {channelError}";
            return false;
        }

        if (channels.Count == 0)
        {
            return false;
        }

        spec = new InstrumentSpec(name, channels, TimeSpan.FromMilliseconds(ms), notes);
        error = null;
        return true;
    }

    private static bool TryParseWithProfile(string raw, string head, string path, out InstrumentSpec spec, out string? error)
    {
        spec = null!;
        var eq = head.IndexOf('=', StringComparison.Ordinal);
        var name = (eq < 0 ? head : head[..eq]).Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            error = $"'{raw}' needs a one-word NAME before @ (e.g. xylo@profile.json).";
            return false;
        }

        if (!ProfileFile.TryLoad(path, out var profile, out error))
        {
            return false;
        }

        // Channels: the command line's, else the one the profile asks for, else all.
        IReadOnlyList<int> channels = profile.Channel is { } own ? [own] : AllChannels;
        if (eq >= 0 && !ChannelList.TryParse(head[(eq + 1)..], out channels, out var channelError))
        {
            error = $"'{raw}': {channelError}";
            return false;
        }

        spec = new InstrumentSpec(name, channels, profile.MaxLatency, profile.Notes) { Profile = profile };
        error = null;
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}={ChannelList.Format(Channels)}@{Latency.TotalMilliseconds:0.#}")
        + (Notes is null ? "" : "/" + NoteList.Format(Notes));

    /// <summary>The arranger's view: the instrument's notes, and its first channel as the one it plays on.</summary>
    public Core.Arranging.BandMember ToBandMember() => new(ToProfile(), Channels[0]);
}
