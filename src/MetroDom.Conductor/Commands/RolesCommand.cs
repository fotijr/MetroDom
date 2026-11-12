using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Core.Arranging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Shows each part's role (ADR 0012: drums, bass, melody…) and where it came from, and saves
/// corrections next to the song so every later <c>arrange</c>/<c>play</c> uses them.
/// </summary>
internal sealed class RolesCommand : Command<RolesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SONG>")]
        [Description("Standard MIDI File (.mid).")]
        public string Path { get; init; } = "";

        [CommandOption("--set <PART=ROLE>")]
        [Description("Save a role for a part, repeatable. PART is the track name, [bold]#TRACK[/] or [bold]#TRACK:CHANNEL[/] (as listed). ROLE is one word, usually one of: " + RoleList + ". Saved to SONG.roles.json.")]
        public string[]? Set { get; init; }

        [CommandOption("--unset <PART>")]
        [Description("Forget a saved role (the part falls back to detection), repeatable.")]
        public string[]? Unset { get; init; }

        public override ValidationResult Validate()
        {
            if (!File.Exists(Path))
            {
                return ValidationResult.Error($"file not found: {Path}");
            }

            foreach (var raw in Set ?? [])
            {
                if (!TryParseSet(raw, out _, out _))
                {
                    return ValidationResult.Error($"--set: '{raw}' is not PART=ROLE with a one-word role (e.g. Lead=melody or #3=guitar).");
                }
            }

            return (Unset ?? []).Any(string.IsNullOrWhiteSpace)
                ? ValidationResult.Error("--unset needs a PART.")
                : ValidationResult.Success();
        }
    }

    private const string RoleList = "drums, bass, guitar, keys, strings, brass, winds, vocals, melody, other";

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var midiFile = MidiFile.Read(settings.Path);
        var mapping = SongRoles.Load(settings.Path);
        if (settings.Set is { Length: > 0 } || settings.Unset is { Length: > 0 })
        {
            var parts = Arranger.Parts(midiFile);
            mapping ??= new RoleMapping();
            foreach (var raw in settings.Set ?? [])
            {
                if (!TryParseSet(raw, out var key, out var role))
                {
                    continue; // Validate already rejected these.
                }

                Warn(key, parts);
                mapping = mapping.With(key, role);
            }

            foreach (var key in settings.Unset ?? [])
            {
                mapping = mapping.With(key, null);
            }

            SongRoles.Save(settings.Path, mapping);
            Ui.Success($"Saved {System.IO.Path.GetFileName(RoleMapping.PathFor(settings.Path))}.");
        }

        AnsiConsole.Write(Table(Arranger.Parts(midiFile, mapping)));
        AnsiConsole.MarkupLine("[grey]Change one with[/] metrodom roles SONG --set \"PART=ROLE\" [grey](PART = name, #TRACK or #TRACK:CHANNEL).[/]");
        return 0;
    }

    internal static Table Table(IReadOnlyList<SongPart> parts)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("[grey]Part[/]")
            .AddColumn("Name")
            .AddColumn(new TableColumn("Notes").RightAligned())
            .AddColumn("Role")
            .AddColumn("[grey]From[/]");
        if (parts.Count == 0)
        {
            table.AddRow("", "[grey](no notes)[/]", "", "", "");
        }

        foreach (var part in parts)
        {
            table.AddRow(
                RoleMapping.KeyFor(part),
                Markup.Escape(part.Name),
                part.NoteCount.ToString(CultureInfo.InvariantCulture),
                $"[bold]{Markup.Escape(part.Role)}[/]",
                $"[grey]{Markup.Escape(Source(part))}[/]");
        }

        return table;
    }

    /// <summary>Why a part has its role, for people: "track name", "GM program 33 (bass)".</summary>
    internal static string Source(SongPart part) => part.RoleSource switch
    {
        RoleSource.Mapping => "saved",
        RoleSource.Channel10 => "channel 10",
        RoleSource.TrackName => "track name",
        RoleSource.Program => string.Create(CultureInfo.InvariantCulture, $"GM program {part.Program + 1}"),
        _ => "nothing to go on",
    };

    internal static bool TryParseSet(string raw, out string key, out string role)
    {
        var eq = raw.LastIndexOf('=');
        key = eq > 0 ? raw[..eq].Trim() : "";
        role = "";
        return key.Length > 0 && Roles.TryNormalize(raw[(eq + 1)..], out role);
    }

    private static void Warn(string key, IReadOnlyList<SongPart> parts)
    {
        var mapping = new RoleMapping().With(key, Roles.Other);
        if (!parts.Any(p => mapping.Find(p) is not null))
        {
            Ui.Warn($"'{key}' matches no part in this song; saved anyway.");
        }
    }
}
