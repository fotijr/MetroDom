using System.Globalization;
using MetroDom.Core;

namespace MetroDom.Conductor.Cli;

/// <summary>
/// A virtual instrument written on the command line as <c>NAME[=CHANNELS]@LATENCY</c>:
/// <c>drums=10@35</c> is "drums" on channel 10 with 35 ms latency, <c>xylo@10</c> plays every
/// channel. Channels use musician numbering (1-16) and may be ranges (<c>1-9,11-16</c>).
/// </summary>
internal sealed record InstrumentSpec(string Name, IReadOnlyList<int> Channels, TimeSpan Latency)
{
    /// <summary>All 16 channels, zero-based.</summary>
    public static readonly IReadOnlyList<int> AllChannels = Enumerable.Range(0, Orchestra.ChannelCount).ToArray();

    /// <summary>The roadmap S1 band: melody at 10 ms on every channel but 10, drums at 35 ms on 10.</summary>
    public static readonly IReadOnlyList<InstrumentSpec> DefaultBand =
    [
        new("xylo", AllChannels.Where(c => c != 9).ToArray(), TimeSpan.FromMilliseconds(10)),
        new("drums", [9], TimeSpan.FromMilliseconds(35)),
    ];

    public InstrumentProfile ToProfile() => new(Name, Latency);

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
        error = $"'{raw}' is not NAME[=CHANNELS]@LATENCY_MS (e.g. drums=10@35).";
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

        spec = new InstrumentSpec(name, channels, TimeSpan.FromMilliseconds(ms));
        error = null;
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}={ChannelList.Format(Channels)}@{Latency.TotalMilliseconds:0.#}");
}
