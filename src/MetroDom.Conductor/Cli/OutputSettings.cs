using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using MetroDom.Core.Safety;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Cli;

/// <summary>Options shared by every command that drives outputs (instruments).</summary>
internal class OutputSettings : CommandSettings
{
    [CommandOption("-o|--output <PORT>")]
    [Description("Output port: index, name, or unique part of a name from [bold]metrodom devices[/] (on Linux the ALSA address works too, e.g. 20:0). Repeat for several instruments, optionally as [bold]PORT=CHANNELS@LATENCY_MS/NOTES[/] (e.g. -o \"xylo=1@10/C4-C6\" -o \"drums=10@35/36,38\"; notes are what [bold]--arrange[/] may use). Without channels a port gets every channel. When omitted, the band is what discovery finds (assigned devices and MetroDom instruments), else prompted for in a terminal.")]
    public string[]? Outputs { get; init; }

    [CommandOption("--no-identify")]
    [Description("Don't ask outputs for their profile. By default an output given without [bold]@LATENCY_MS[/] or [bold]/NOTES[/] is asked who it is, and a MetroDom instrument's profile fills in what's missing (ADR 0007).")]
    public bool NoIdentify { get; init; }

    [CommandOption("--assignments <FILE>")]
    [Description("Where remembered assignments live (see [bold]metrodom devices --assign[/]). Default: $METRODOM_ASSIGNMENTS, else metrodom/assignments.json in your app data folder. Without [bold]-o[/], the band is every assigned device plus every MetroDom instrument found.")]
    public string? Assignments { get; init; }

    [CommandOption("-m|--mute <CHANNELS>")]
    [Description("Channels to mute, 1-16, comma separated, ranges allowed (e.g. 10 for drums, 1-9).")]
    public string? Mute { get; init; }

    [CommandOption("--max-velocity <1-127>")]
    [Description("Safety: turn down any note louder than this.")]
    public int? MaxVelocity { get; init; }

    [CommandOption("--max-rate <NOTES>")]
    [Description("Safety: most notes per second any one output may play. Extra notes are held back.")]
    public int? MaxRate { get; init; }

    [CommandOption("--quiet <HH:MM-HH:MM[@VELOCITY]>")]
    [Description("Safety: quiet hours, local time, may cross midnight. No new notes inside them, or with [bold]@VELOCITY[/] notes are turned down to that (e.g. [bold]--quiet 23:00-07:00@40[/]).")]
    public string? Quiet { get; init; }

    /// <summary>Policy limits for the safety governors, valid after <see cref="Validate"/>.</summary>
    public SafetyLimits Limits => new()
    {
        MaxVelocity = MaxVelocity,
        MaxNotesPerSecond = MaxRate,
        Quiet = QuietHoursSpec.TryParse(Quiet, out var quiet, out _) ? quiet : null,
    };

    /// <summary>Zero-based muted channels, valid after <see cref="Validate"/>.</summary>
    public IReadOnlyList<int> MutedChannels => ChannelList.TryParse(Mute, out var channels, out _) ? channels : [];

    /// <summary>Parsed <c>--output</c> values, valid after <see cref="Validate"/>. Empty means "prompt".</summary>
    public IReadOnlyList<OutputSpec> OutputSpecs =>
        (Outputs ?? []).Select(o => OutputSpec.TryParse(o, out var spec, out _) ? spec : null).OfType<OutputSpec>().ToArray();

    public override ValidationResult Validate()
    {
        if (!ChannelList.TryParse(Mute, out _, out var error))
        {
            return ValidationResult.Error($"--mute: {error}");
        }

        if (MaxVelocity is < 1 or > 127)
        {
            return ValidationResult.Error("--max-velocity must be 1-127.");
        }

        if (MaxRate is < 1)
        {
            return ValidationResult.Error("--max-rate must be at least 1 note per second.");
        }

        if (!QuietHoursSpec.TryParse(Quiet, out _, out error))
        {
            return ValidationResult.Error($"--quiet: {error}");
        }

        foreach (var output in Outputs ?? [])
        {
            if (!OutputSpec.TryParse(output, out _, out error))
            {
                return ValidationResult.Error($"--output: {error}");
            }
        }

        return ValidationResult.Success();
    }
}

/// <summary>
/// One <c>--output</c>: <c>PORT[=CHANNELS][@LATENCY_MS][/NOTES]</c>. <c>"MetroDom drums=10@35/36,38"</c>
/// sends channel 10 to the port matching "MetroDom drums", 35 ms early, and tells the arranger it
/// can play notes 36 and 38. A plain port gets every channel; its latency and notes come from the
/// instrument's profile if it sends one (<see cref="WantsProfile"/>), else none and every note.
/// </summary>
internal sealed partial record OutputSpec(string Port, IReadOnlyList<int> Channels, TimeSpan? Latency = null, IReadOnlySet<int>? Notes = null)
{
    /// <summary>Whether a profile from the instrument would add anything the command line didn't say.</summary>
    public bool WantsProfile => Latency is null || Notes is null;

    public static bool TryParse(string raw, out OutputSpec spec, out string? error)
    {
        spec = null!;
        IReadOnlySet<int>? notes = null;
        if (NotesSuffix().Match(raw) is { Success: true } suffix)
        {
            if (!NoteList.TryParse(suffix.Groups["notes"].Value, out var parsedNotes, out var noteError))
            {
                error = $"'{raw}': {noteError}";
                return false;
            }

            notes = parsedNotes;
            raw = raw[..suffix.Index];
        }

        var match = Pattern().Match(raw);
        if (!match.Success || match.Groups["port"].Value.TrimStart() is ['=' or '@', ..])
        {
            error = $"'{raw}' is not PORT[=CHANNELS][@LATENCY_MS] (e.g. drums=10@35).";
            return false;
        }

        IReadOnlyList<int> channels = InstrumentSpec.AllChannels;
        if (match.Groups["ch"].Success && !ChannelList.TryParse(match.Groups["ch"].Value, out channels, out var channelError))
        {
            error = $"'{raw}': {channelError}";
            return false;
        }

        var latency = 0.0;
        if (match.Groups["lat"].Success
            && (!double.TryParse(match.Groups["lat"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out latency) || latency > 10_000))
        {
            error = $"'{raw}': latency must be 0-10000 ms.";
            return false;
        }

        if (channels.Count == 0)
        {
            error = $"'{raw}': no channels.";
            return false;
        }

        spec = new OutputSpec(match.Groups["port"].Value.Trim(), channels, match.Groups["lat"].Success ? TimeSpan.FromMilliseconds(latency) : null, notes);
        error = null;
        return true;
    }

    [GeneratedRegex(@"^(?<port>.*?\S.*?)(?:=(?<ch>[0-9,\-\s]+))?(?:@(?<lat>[0-9]+(?:\.[0-9]+)?)\s*(?:ms)?)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    /// <summary>"/NOTES" at the end: note numbers, names (C4, F#3, Bb2), commas and ranges.</summary>
    [GeneratedRegex(@"/(?<notes>[A-Ga-g0-9#,\-\s]+)$")]
    private static partial Regex NotesSuffix();
}

/// <summary>Quiet hours as written on the command line: <c>HH:MM-HH:MM[@VELOCITY]</c>.</summary>
internal static partial class QuietHoursSpec
{
    public static bool TryParse(string? raw, out QuietHours? quiet, out string? error)
    {
        quiet = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var match = Pattern().Match(raw);
        if (!match.Success
            || !TimeOnly.TryParseExact(match.Groups["start"].Value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(match.Groups["end"].Value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            error = $"'{raw}' is not HH:MM-HH:MM[@VELOCITY] (e.g. 23:00-07:00 or 22:00-08:00@40).";
            return false;
        }

        var velocity = 0;
        if (match.Groups["vel"].Success
            && (!int.TryParse(match.Groups["vel"].Value, CultureInfo.InvariantCulture, out velocity) || velocity > 127))
        {
            error = $"'{raw}': velocity must be 0-127.";
            return false;
        }

        if (start == end)
        {
            error = $"'{raw}': start and end are the same.";
            return false;
        }

        quiet = new QuietHours(start, end, velocity);
        return true;
    }

    [GeneratedRegex(@"^\s*(?<start>\d{1,2}:\d{2})\s*-\s*(?<end>\d{1,2}:\d{2})\s*(?:@\s*(?<vel>\d+))?\s*$")]
    private static partial Regex Pattern();
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
