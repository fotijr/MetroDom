using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.MusicTheory;
using MetroDom.Conductor.Cli;
using MetroDom.Core.Bodies;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>The MetroDom checkout the CLI runs in, found by walking up from the current directory.</summary>
internal static class Workspace
{
    /// <summary>The nearest directory holding both <c>Instruments/</c> and <c>hardware/</c>, or <c>null</c>.</summary>
    public static string? FindRoot(string? from = null)
    {
        for (var dir = new DirectoryInfo(from ?? Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Instruments")) && Directory.Exists(Path.Combine(dir.FullName, "hardware")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>Every <c>Instruments/*/body.json</c> under <paramref name="root"/>, in name order.</summary>
    public static IReadOnlyList<string> InstrumentBodies(string root) =>
        [.. Directory.EnumerateDirectories(Path.Combine(root, "Instruments"))
            .Select(d => Path.Combine(d, "body.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)];
}

/// <summary>
/// What <c>metrodom instrument new</c> writes: a folder with <c>instrument.toml</c>, a
/// <c>body.json</c> a brain will accept, and a README to fill in. Pure, so tests can check it.
/// </summary>
internal static class InstrumentScaffold
{
    /// <summary>GPIO pins a new GPIO body uses, in order: clear of the Qwiic bus, /OE and the LED pin.</summary>
    private static readonly int[] GpioPins = [8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 26];

    /// <summary>Safe first values for a small solenoid; the guide says how to tune them.</summary>
    public const int PulseMs = 30;
    public const int MinPulseMs = 12;
    public const int MinIntervalMs = 60;

    public sealed record Request(string Id, string Name, IReadOnlySet<int> Notes, BodyDriver Driver, string Actuator, string? Plays);

    public sealed record Files(BodyDefinition Body, string Toml, string BodyJson, string Readme);

    /// <summary>The default USB name for <paramref name="id"/>: "xylophone-alto" → "MetroDom xylophone alto".</summary>
    public static string DefaultName(string id) => $"MetroDom {id.Replace('-', ' ')}";

    /// <summary>The files for <paramref name="request"/>, or a reason it can't be built.</summary>
    public static Files Build(Request request, string readmeTemplate)
    {
        ArgumentNullException.ThrowIfNull(request);
        var notes = request.Notes.Order().ToArray();
        if (notes.Length > BodyImage.MaxActuators)
        {
            throw new UsageException($"a body drives at most {BodyImage.MaxActuators} notes; {notes.Length} given.");
        }

        var body = new BodyDefinition(
            request.Id,
            request.Name,
            request.Driver,
            request.Driver == BodyDriver.Pca9685 ? 0x40 : 0,
            [.. notes.Select((n, i) => new BodyActuator(n, request.Driver == BodyDriver.Pca9685 ? i : GpioPins[i], PulseMs, MinPulseMs, MinIntervalMs, 0))])
        {
            // Two at once is a xylophone dyad; more needs a supply sized for it, a deliberate edit.
            Polyphony = Math.Min(notes.Length, 2),
        };
        if (BodyImage.Validate(body) is { } problem)
        {
            throw new UsageException(problem);
        }

        return new Files(body, Toml(request, notes.Length), BodyImage.ToJson(body), Readme(request, body, readmeTemplate));
    }

    private static string Toml(Request r, int count)
    {
        var s = new StringBuilder();
        s.AppendLine("# Which shared hardware modules this instrument is built from (ADR 0009).");
        s.AppendLine("# Written by `metrodom instrument new`. Check it with python3 scripts/check_hardware.py.");
        s.AppendLine(CultureInfo.InvariantCulture, $"id = \"{r.Id}\"");
        s.AppendLine(CultureInfo.InvariantCulture, $"name = \"{r.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
        s.AppendLine("status = \"planned\"         # planned | prototype | proven | deprecated (like modules)");
        s.AppendLine(r.Plays is { } plays
            ? $"plays = \"{plays}\"         # id in hardware/inventory/musical-instruments.csv"
            : "# plays = \"\"              # id in hardware/inventory/musical-instruments.csv");
        s.AppendLine("requires_equipment = []    # ids from hardware/inventory/equipment.csv it needs to play");
        Module("brain-pico", 1);
        Module("body-backplane", 1);
        Module("power-entry", 1);
        Module("driver-mosfet-8ch", (count + 7) / 8);
        Module(r.Actuator, count);
        Module("frame-2020", 1);
        return s.ToString();

        void Module(string id, int qty)
        {
            s.AppendLine();
            s.AppendLine("[[modules]]");
            s.AppendLine(CultureInfo.InvariantCulture, $"id = \"{id}\"");
            s.AppendLine(CultureInfo.InvariantCulture, $"qty = {qty}");
        }
    }

    private static string Readme(Request r, BodyDefinition body, string template)
    {
        var output = body.Driver == BodyDriver.Pca9685 ? "PCA9685 channel" : "GPIO";
        var notes = new StringBuilder();
        notes.AppendLine(CultureInfo.InvariantCulture, $"| Note | {output} | Actuator |");
        notes.AppendLine("| --- | --- | --- |");
        foreach (var a in body.Notes)
        {
            notes.AppendLine(CultureInfo.InvariantCulture, $"| {Note.Get((SevenBitNumber)(byte)a.Note)} ({a.Note}) | {a.Output} | {r.Actuator} |");
        }

        return template
            .Replace("{{name}}", r.Name, StringComparison.Ordinal)
            .Replace("{{id}}", r.Id, StringComparison.Ordinal)
            .Replace("{{plays}}", r.Plays ?? "(fill in)", StringComparison.Ordinal)
            .Replace("{{notes}}", notes.ToString().TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>The README template, <c>Instruments/_template/README.md</c>, built into the CLI.</summary>
    public static string ReadmeTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MetroDom.Conductor.Instruments.README.md")
            ?? throw new InvalidOperationException("the instrument README template isn't built in.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Starts a new instrument: its folder, module list, body file and README.</summary>
internal sealed class InstrumentNewCommand : Command<InstrumentNewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Its id and folder name: 1-32 of a-z, 0-9 and '-' (e.g. [bold]snare[/], [bold]xylophone-alto[/]).")]
        public string Id { get; init; } = "";

        [CommandOption("--name <NAME>")]
        [Description("What DAWs and [bold]metrodom devices[/] call it (the USB name). Default: \"MetroDom\" and the id.")]
        public string? Name { get; init; }

        [CommandOption("-n|--notes <NOTES>")]
        [Description("The notes it plays, one actuator each: numbers or names with ranges ([bold]C5-E5[/], [bold]38[/], [bold]72,74,76[/]). Default: 60.")]
        public string Notes { get; init; } = "60";

        [CommandOption("--driver <DRIVER>")]
        [Description("[bold]pca9685[/] (the body-backplane, default) or [bold]gpio[/] (brain pins, a bench rig).")]
        public string Driver { get; init; } = "pca9685";

        [CommandOption("--actuator <MODULE>")]
        [Description("The module on each note, from hardware/modules. Default: [bold]striker-solenoid[/].")]
        public string Actuator { get; init; } = "striker-solenoid";

        [CommandOption("--plays <ID>")]
        [Description("The real instrument it plays, from hardware/inventory/musical-instruments.csv (e.g. [bold]drum-kit[/]).")]
        public string? Plays { get; init; }

        [CommandOption("--dir <DIR>")]
        [Description("Where to make its folder. Default: Instruments/ in this MetroDom checkout.")]
        public string? Dir { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var root = Workspace.FindRoot();
        var parent = settings.Dir ?? (root is null
            ? throw new UsageException("not inside a MetroDom checkout (no Instruments/ and hardware/ above here). Run it from the repo, or give --dir.")
            : Path.Combine(root, "Instruments"));
        var folder = Path.Combine(parent, settings.Id);
        if (Directory.Exists(folder))
        {
            throw new UsageException($"{folder} already exists. Pick another id, or edit that instrument.");
        }

        if (!NoteList.TryParse(settings.Notes, out var notes, out var noteError))
        {
            throw new UsageException($"--notes: {noteError}");
        }

        var driver = settings.Driver.ToLowerInvariant() switch
        {
            "pca9685" => BodyDriver.Pca9685,
            "gpio" => BodyDriver.Gpio,
            _ => throw new UsageException($"--driver must be pca9685 or gpio, not '{settings.Driver}'."),
        };
        if (root is not null)
        {
            CheckAgainstRepo(root, settings);
        }

        var request = new InstrumentScaffold.Request(settings.Id, settings.Name ?? InstrumentScaffold.DefaultName(settings.Id), notes, driver, settings.Actuator, settings.Plays);
        var files = InstrumentScaffold.Build(request, InstrumentScaffold.ReadmeTemplate());

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "instrument.toml"), files.Toml);
        File.WriteAllText(Path.Combine(folder, "body.json"), files.BodyJson);
        File.WriteAllText(Path.Combine(folder, "README.md"), files.Readme);

        BodyFile.Show(files.Body, BodyImage.Encode(files.Body));
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), folder);
        var shown = relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFullPath(folder) : relative;
        Ui.Success($"Made {shown}/ with instrument.toml, body.json and README.md. A brain will accept this body.");
        AnsiConsole.MarkupLine("[bold]Next[/] [grey](docs/references/new-instrument.md walks through each step)[/]:");
        var body = Path.Combine(shown, "body.json");
        AnsiConsole.MarkupLine($"  1. Tune pulses and limits in body.json, then [bold]metrodom body check {Markup.Escape(Quote(body))}[/]");
        AnsiConsole.MarkupLine($"  2. Play it without hardware: [bold]metrodom virtual -i {Markup.Escape(Quote($"{settings.Id}@0/{NoteList.Format(notes)}"))}[/]");
        AnsiConsole.MarkupLine("  3. See what to buy or print: [bold]python3 scripts/check_hardware.py --buildable[/]");
        AnsiConsole.MarkupLine($"  4. On the bench: [bold]metrodom body write \"MetroDom brain\" {Markup.Escape(Quote(body))}[/]");
        return 0;
    }

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? $"\"{s}\"" : s;

    /// <summary>The module and the played instrument must exist in this checkout's hardware records.</summary>
    private static void CheckAgainstRepo(string root, Settings settings)
    {
        var modules = Path.Combine(root, "hardware", "modules");
        if (Directory.Exists(modules) && !File.Exists(Path.Combine(modules, settings.Actuator, "module.toml")))
        {
            var known = Directory.EnumerateDirectories(modules)
                .Select(Path.GetFileName)
                .Where(n => n is not null && !n.StartsWith('_') && File.Exists(Path.Combine(modules, n, "module.toml")) &&
                    File.ReadLines(Path.Combine(modules, n, "module.toml")).Any(l => l.StartsWith("job", StringComparison.Ordinal) &&
                        (l.Contains("\"strike\"", StringComparison.Ordinal) || l.Contains("\"press\"", StringComparison.Ordinal) ||
                         l.Contains("\"pluck\"", StringComparison.Ordinal) || l.Contains("\"damp\"", StringComparison.Ordinal))))
                .Order(StringComparer.Ordinal);
            throw new UsageException($"--actuator: no module '{settings.Actuator}' in hardware/modules. Actuator modules: {string.Join(", ", known)}.");
        }

        var instruments = Path.Combine(root, "hardware", "inventory", "musical-instruments.csv");
        if (settings.Plays is { } plays && File.Exists(instruments))
        {
            var ids = File.ReadLines(instruments).Skip(1).Select(l => l.Split(',')[0]).Where(id => id.Length > 0).ToList();
            if (!ids.Contains(plays, StringComparer.Ordinal))
            {
                throw new UsageException($"--plays: '{plays}' isn't in hardware/inventory/musical-instruments.csv. Known: {string.Join(", ", ids)}. Add a row first if it's new.");
            }
        }
    }
}
