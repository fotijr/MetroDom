using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Lighting;
using MetroDom.Core.Lighting;
using MetroDom.Core.Policy;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Checks a light profile and shows its patch, or holds one look on the rig over sACN, so a rig
/// can be tested without a song (ADR 0011, exec plan 0003 V2).
/// </summary>
internal sealed class LightsCommand : Command<LightsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<RIG>")]
        [Description("Light profile (metrodom.light-profile/0 JSON).")]
        public string Path { get; init; } = "";

        [CommandOption("--scene <NOTE|NAME>")]
        [Description("Hold this scene on the rig (its note, e.g. [bold]36[/], or name) until Ctrl+C.")]
        public string? Scene { get; init; }

        [CommandOption("--safe")]
        [Description("Hold the safe look (what panic shows) until Ctrl+C.")]
        public bool Safe { get; init; }

        [CommandOption("--master <0-255>")]
        [Description("Master dimmer: scales every intensity channel. Default 255.")]
        [DefaultValue(255)]
        public int Master { get; init; } = 255;

        [CommandOption("--to <HOST>")]
        [Description("Send to this host instead of the profile's destination (e.g. a WLED's IP). Default: the profile's, else sACN multicast.")]
        public string? To { get; init; }

        [CommandOption("--policy <FILE>")]
        [Description("Policy profile (metrodom.policy/0) whose [bold]lights[/] limits apply: brightness cap, quiet hours, strobe. Default: no strobe, full brightness.")]
        public string? Policy { get; init; }

        public LightLimits Limits =>
            Policy is not null && File.Exists(Policy) && PolicyProfile.TryParse(File.ReadAllText(Policy), out var policy, out _) ? policy.Lights : LightLimits.Default;

        [CommandOption("--for <SECONDS>")]
        [Description("Stop by itself after this long (for scripts).")]
        public double? For { get; init; }

        public LightProfile? Profile =>
            File.Exists(Path) && LightProfileJson.TryParse(File.ReadAllText(Path), out var profile, out _) ? profile : null;

        public override ValidationResult Validate()
        {
            if (!File.Exists(Path))
            {
                return ValidationResult.Error($"file not found: {Path}");
            }

            if (!LightProfileJson.TryParse(File.ReadAllText(Path), out var profile, out var error))
            {
                return ValidationResult.Error($"{Path}: {error}");
            }

            if (Policy is not null && !File.Exists(Policy))
            {
                return ValidationResult.Error($"--policy: file not found: {Policy}");
            }

            if (Policy is not null && !PolicyProfile.TryParse(File.ReadAllText(Policy), out _, out var policyError))
            {
                return ValidationResult.Error($"--policy: {policyError}");
            }

            if (Scene is not null && Safe)
            {
                return ValidationResult.Error("--scene and --safe both pick the look; give one.");
            }

            if (Scene is not null && FindScene(profile, Scene) is null)
            {
                return ValidationResult.Error($"--scene: no scene '{Scene}'. Scenes: {string.Join(", ", profile.Scenes.Select(s => $"{s.Note} {s.Name}"))}.");
            }

            return Master is < 0 or > 255 ? ValidationResult.Error("--master must be 0-255.")
                : For is <= 0 ? ValidationResult.Error("--for must be more than 0 seconds.")
                : ValidationResult.Success();
        }
    }

    internal static LightScene? FindScene(LightProfile profile, string what) =>
        int.TryParse(what, NumberStyles.None, CultureInfo.InvariantCulture, out var note)
            ? profile.SceneFor(note)
            : profile.Scenes.FirstOrDefault(s => string.Equals(s.Name, what, StringComparison.OrdinalIgnoreCase));

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var profile = settings.Profile!;
        if (settings.To is not null)
        {
            profile = profile with { Output = profile.Output with { Destination = settings.To } };
        }

        AnsiConsole.Write(Patch(profile));
        if (settings.Scene is null && !settings.Safe)
        {
            AnsiConsole.MarkupLine("[grey]Add --scene NOTE or --safe to show a look on the rig.[/]");
            return 0;
        }

        var (name, look) = settings.Safe ? ("safe look", profile.SafeLook) : FindScene(profile, settings.Scene!) is { } scene ? (scene.Name, scene.Look) : ("", LightLook.Blackout);
        using var stop = new ManualResetEventSlim();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stop.Set();
        }

        Console.CancelKeyPress += OnCancel;
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, e =>
        {
            e.Cancel = true;
            stop.Set();
        });
        long sent;
        using (var output = new SacnOutput(new UdpSacnSocket(), profile.Output, $"MetroDom {profile.Name}", log: Ui.Warn))
        {
            // The venue's caps apply here too; the safe look ignores --master, as in play.
            var limits = settings.Limits;
            var local = TimeOnly.FromDateTime(DateTime.Now);
            var cap = limits.BrightnessAt(local);
            var master = settings.Safe ? cap : (byte)((settings.Master * cap + 127) / 255);
            output.Show(LightRenderer.Render(profile, look, master, !settings.Safe && limits.StrobeAt(local)));
            if (!limits.IsDefault)
            {
                Console.WriteLine($"Policy: {limits.Describe()}.");
            }

            var where = string.Join(", ", profile.Universes.Select(u => $"universe {u} → {output.DestinationFor(u)}"));
            Console.WriteLine($"Showing {name} on {where} (sACN, priority {profile.Output.Priority}). Ctrl+C to stop.");
            if (settings.For is { } seconds)
            {
                stop.Wait(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
            else
            {
                stop.Wait(cancellationToken);
            }

            Console.CancelKeyPress -= OnCancel;
            sent = output.Sent;
        }

        Ui.Success($"Stopped: {sent} packets, then stream terminated (receivers hold or fall back to their own look).");
        return 0;
    }

    internal static Table Patch(LightProfile profile)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]{Markup.Escape(profile.Name)}[/] [grey]· lights on channel {profile.Channel + 1} · master CC {profile.MasterCc} · {profile.Latency.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)} ms[/]")
            .AddColumn("Fixture")
            .AddColumn(new TableColumn("Universe").RightAligned())
            .AddColumn(new TableColumn("DMX").RightAligned())
            .AddColumn("Channels");
        foreach (var f in profile.Fixtures)
        {
            var channels = string.Join(" ", f.Channels) + (f.Count > 1 ? $" × {f.Count}" : "");
            table.AddRow(Markup.Escape(f.Id), f.Universe.ToString(CultureInfo.InvariantCulture), $"{f.Address}-{f.Address + f.Footprint - 1}", Markup.Escape(channels));
        }

        var scenes = profile.Scenes.Count == 0 ? "[grey](no scenes)[/]" : string.Join(" · ", profile.Scenes.Select(s => $"{s.Note} {Markup.Escape(s.Name)}"));
        table.Caption($"[grey]Scenes:[/] {scenes}");
        return table;
    }
}
