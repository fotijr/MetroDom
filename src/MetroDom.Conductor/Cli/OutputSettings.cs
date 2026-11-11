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
    [Description("Channels to mute, 1-16, comma separated (e.g. 10 for drums).")]
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
            if (!int.TryParse(part, out var channel) || channel is < 1 or > 16)
            {
                error = $"'{part}' is not a MIDI channel (1-16).";
                return false;
            }

            parsed.Add(channel - 1);
        }

        channels = parsed.ToArray();
        return true;
    }

    /// <summary>Formats 0-based channels back to musician numbering: "10,11".</summary>
    public static string Format(IEnumerable<int> channels) => string.Join(",", channels.Select(c => c + 1));
}
