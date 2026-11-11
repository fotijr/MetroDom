using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Cli;

/// <summary>Options shared by every command that drives an output (instruments).</summary>
internal class OutputSettings : CommandSettings
{
    [CommandOption("-o|--output <PORT>")]
    [Description("Output port: index, name, or unique part of a name from [bold]metrodom devices[/] (on Linux the ALSA address works too, e.g. 20:0). Prompted for when omitted in a terminal.")]
    public string? Output { get; init; }

    [CommandOption("-m|--mute <CHANNELS>")]
    [Description("Channels to mute, 1-16, comma separated, ranges allowed (e.g. 10 for drums, 1-9).")]
    public string? Mute { get; init; }

    /// <summary>Zero-based muted channels, valid after <see cref="Validate"/>.</summary>
    public IReadOnlyList<int> MutedChannels => ChannelList.TryParse(Mute, out var channels, out _) ? channels : [];

    public override ValidationResult Validate() =>
        ChannelList.TryParse(Mute, out _, out var error)
            ? ValidationResult.Success()
            : ValidationResult.Error($"--mute: {error}");
}

/// <summary>Channel lists as musicians write them (1-16), stored as MIDI's 0-15.</summary>
internal static class ChannelList
{
    public static bool TryParse(string? raw, out IReadOnlyList<int> channels, out string? error)
    {
        channels = [];
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var parsed = new SortedSet<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!TryChannel(bounds[0], out var first) || !TryChannel(bounds[^1], out var last) || last < first)
            {
                error = $"'{part}' is not a MIDI channel (1-16) or range (e.g. 1-9).";
                return false;
            }

            for (var channel = first; channel <= last; channel++)
            {
                parsed.Add(channel - 1);
            }
        }

        channels = parsed.ToArray();
        return true;
    }

    private static bool TryChannel(string text, out int channel) =>
        int.TryParse(text, out channel) && channel is >= 1 and <= 16;

    /// <summary>Formats 0-based channels back to musician numbering: "10,11", runs of 3+ as "1-9".</summary>
    public static string Format(IEnumerable<int> channels)
    {
        var sorted = channels.Distinct().Order().ToArray();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Length;)
        {
            var end = i;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
            {
                end++;
            }

            if (end - i >= 2)
            {
                parts.Add($"{sorted[i] + 1}-{sorted[end] + 1}");
            }
            else
            {
                parts.AddRange(sorted[i..(end + 1)].Select(c => (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            i = end + 1;
        }

        return string.Join(",", parts);
    }
}
