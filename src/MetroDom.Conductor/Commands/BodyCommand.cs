using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.MusicTheory;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Bodies;
using MetroDom.Core.Calibration;
using MetroDom.Core.Discovery;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>Shared by <c>body check</c> and <c>body write</c>: reading and showing a body file.</summary>
internal static class BodyFile
{
    public static BodyDefinition Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new UsageException($"no body file at {path}.");
        }

        return BodyImage.TryParseJson(File.ReadAllText(path), out var body, out var error)
            ? body
            : throw new UsageException($"{path}: {error}");
    }

    /// <summary>The body a brain booted from; a usage error when it has none or doesn't answer.</summary>
    public static BodyDefinition ReadFrom(IMidiBackend backend, MidiPortInfo port)
    {
        var (answered, record) = BodyWriter.Read(backend, port, BodyWriter.DefaultTimeout);
        if (!answered)
        {
            throw new UsageException($"{port.Name} didn't answer the Body Read. Is it running the MetroDom brain firmware?");
        }

        if (record is null)
        {
            throw new UsageException($"{port.Name} has no body record: it runs its compiled-in bench body, or a silent one (see metrodom devices --identify). Give a body file.");
        }

        return BodyImage.TryDecode(record, out var body, out var error)
            ? body
            : throw new UsageException($"{port.Name} sent a body record that doesn't read: {error}");
    }

    public static void Show(BodyDefinition body, byte[] record)
    {
        var driver = body.Driver == BodyDriver.Pca9685
            ? string.Create(CultureInfo.InvariantCulture, $"PCA9685 at 0x{body.Address:X2}")
            : "GPIO pins";
        AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"[bold]{Markup.Escape(body.Name)}[/] [grey]({Markup.Escape(body.Id)})[/] · {driver} · {(body.Channel is { } c ? $"channel {c + 1}" : "any channel")} · polyphony {body.Polyphony}"));
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("Note").AddColumn("Output").AddColumn("Pulse").AddColumn("Repeat ≥").AddColumn("Latency");
        if (body.Accent is not null)
        {
            table.AddColumn("LEDs");
        }

        foreach (var a in body.Notes)
        {
            var pulse = a.MinPulseMs > 0 && a.MinPulseMs < a.PulseMs
                ? string.Create(CultureInfo.InvariantCulture, $"{a.MinPulseMs}-{a.PulseMs} ms [grey](by velocity)[/]")
                : string.Create(CultureInfo.InvariantCulture, $"{a.PulseMs} ms");
            List<string> row =
            [
                string.Create(CultureInfo.InvariantCulture, $"{Note.Get((SevenBitNumber)(byte)a.Note)} [grey]({a.Note})[/]"),
                a.Output.ToString(CultureInfo.InvariantCulture),
                pulse,
                string.Create(CultureInfo.InvariantCulture, $"{a.MinIntervalMs} ms"),
                a.LatencyMs > 0 ? string.Create(CultureInfo.InvariantCulture, $"{a.LatencyMs} ms") : "[grey]not measured[/]",
            ];
            if (body.Accent is not null)
            {
                row.Add(Leds(a.Led));
            }

            table.AddRow([.. row]);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"[grey]Safety: pulse ≤ {body.MaxOnMs} ms, duty ≤ {body.MaxDutyPercent}% of {body.DutyWindowMs} ms, watchdog {body.WatchdogMs} ms. Record: {record.Length} bytes, {BodyWrite.Chunks(record).Count} SysEx messages.[/]"));
        if (body.Accent is { } strip)
        {
            AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                $"[grey]Accent LEDs: {strip.Leds} on GPIO {BodyImage.LedPin}, flash {strip.FlashMs} ms, at most {strip.MaxBrightnessPercent}% bright.[/]"));
        }
    }

    /// <summary>"0-2 ■ #FF4000", the swatch in the LED's own color.</summary>
    internal static string Leds(AccentLed? led) => led is null
        ? "[grey]none[/]"
        : string.Create(CultureInfo.InvariantCulture,
            $"{(led.Count == 1 ? $"{led.First}" : $"{led.First}-{led.First + led.Count - 1}")} [{led.Hex}]■[/] [grey]{led.Hex}[/]");
}

/// <summary>Shows the body record a brain booted from, and saves it as a body file.</summary>
internal sealed class BodyReadCommand : Command<BodyReadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PORT>")]
        [Description("The brain's port: index, name, or unique part of a name from [bold]metrodom devices[/].")]
        public string Port { get; init; } = "";

        [CommandOption("--out <FILE>")]
        [Description("Save it as a [bold]metrodom.body/0[/] file (a backup, or a start for editing).")]
        public string? Out { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var backend = MidiDevices.Backend.Value;
        var port = MidiDevices.FindOutput(settings.Port);
        if (Identifier.Identify(backend, Identifier.DefaultTimeout, [port])[0].Kind != PortKind.MetroDom)
        {
            throw new UsageException($"{port.Name} didn't identify as a MetroDom instrument, so it gets no Body Read.");
        }

        var body = BodyFile.ReadFrom(backend, port);
        BodyFile.Show(body, BodyImage.Encode(body));
        if (settings.Out is { } path)
        {
            File.WriteAllText(path, BodyImage.ToJson(body));
            Ui.Success($"Saved {path}.");
        }

        return 0;
    }
}

/// <summary>Checks a body file the way a brain will, and shows what the instrument becomes.</summary>
internal sealed class BodyCheckCommand : Command<BodyCheckCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<FILE>")]
        [Description("A [bold]metrodom.body/0[/] JSON file (see docs/references/body-record.md).")]
        public string File { get; init; } = "";

        [CommandOption("--hex <OUT>")]
        [Description("Also write the record as hex, the bytes a brain stores (fixtures, or an EEPROM programmer).")]
        public string? Hex { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var body = BodyFile.Load(settings.File);
        var record = BodyImage.Encode(body);
        BodyFile.Show(body, record);
        if (settings.Hex is { } path)
        {
            File.WriteAllText(path, BodyImage.ToHex(record) + "\n");
            Ui.Success($"Wrote {path}.");
        }

        Ui.Success("A brain will accept this body.");
        return 0;
    }
}

/// <summary>
/// Writes a body file into a brain's body EEPROM over MetroDom SysEx (ADR 0006 §3). The brain
/// checks it, stores it, reads it back and restarts as that instrument.
/// </summary>
internal sealed class BodyWriteCommand : Command<BodyWriteCommand.Settings>
{
    /// <summary>How long to wait for a restarted brain to answer again.</summary>
    private static readonly TimeSpan ComeBack = TimeSpan.FromSeconds(5);

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PORT>")]
        [Description("The brain's port: index, name, or unique part of a name from [bold]metrodom devices[/].")]
        public string Port { get; init; } = "";

        [CommandArgument(1, "[FILE]")]
        [Description("A [bold]metrodom.body/0[/] JSON file. Checked here first; nothing is sent if it isn't valid. Omitted: the body the brain holds now (with [bold]--calibrated[/]).")]
        public string? File { get; init; }

        [CommandOption("--calibrated")]
        [Description("Put the latencies [bold]metrodom calibrate[/] measured for this port into the body, so it carries them to any Conductor.")]
        public bool Calibrated { get; init; }

        [CommandOption("--calibrations <FILE>")]
        [Description("The calibrations file to read with [bold]--calibrated[/]. Default: $METRODOM_CALIBRATIONS, else the one [bold]calibrate[/] saves.")]
        public string? Calibrations { get; init; }

        public override ValidationResult Validate() =>
            File is null && !Calibrated
                ? ValidationResult.Error("give a body FILE, or --calibrated to update the body the brain holds.")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var backend = MidiDevices.Backend.Value;
        var port = MidiDevices.FindOutput(settings.Port);

        // MetroDom SysEx goes only to ports that said they are MetroDom (7D is shared).
        var before = Identifier.Identify(backend, Identifier.DefaultTimeout, [port])[0];
        if (before.Kind != PortKind.MetroDom)
        {
            throw new UsageException($"{port.Name} didn't identify as a MetroDom instrument, so it gets no Body Write. Check it with metrodom devices --identify.");
        }

        var held = settings.File is null ? BodyFile.ReadFrom(backend, port) : null;
        var body = held ?? BodyFile.Load(settings.File!);
        if (settings.Calibrated)
        {
            var path = CalibrationStore.Resolve(settings.Calibrations);
            var measured = CalibrationStore.Load(path, Ui.Warn).Find(port.Name)
                ?? throw new UsageException($"no calibration for {port.Name} in {path}. Run metrodom calibrate -o \"{port.Name}\" first.");
            var calibrated = BodyImage.WithMeasured(body, measured);
            foreach (var (old, fresh) in body.Notes.Zip(calibrated.Notes).Where(p => p.First.LatencyMs != p.Second.LatencyMs))
            {
                Ui.Note(string.Create(CultureInfo.InvariantCulture,
                    $"{Note.Get((SevenBitNumber)(byte)fresh.Note)}: latency {(old.LatencyMs > 0 ? $"{old.LatencyMs} ms" : "not measured")} → {fresh.LatencyMs} ms (measured {measured.MeasuredAt.LocalDateTime:g})"));
            }

            body = calibrated;
        }

        var record = BodyImage.Encode(body);
        if (held is not null && BodyImage.Encode(held).AsSpan().SequenceEqual(record))
        {
            Ui.Success($"{port.Name} already holds this body; nothing to write.");
            return 0;
        }

        BodyFile.Show(body, record);
        var was = before.Profile?.Name ?? "an instrument with no profile";
        Ui.Note($"{port.Name} is {was} now.");
        if (!AskForTheButton(before, cancellationToken))
        {
            return 1;
        }

        Ui.Note($"Writing {body.Name}…");
        var reply = BodyWriter.Write(backend, port, record, BodyWriter.DefaultTimeout);
        if (reply is null)
        {
            Ui.Fail($"{port.Name} didn't answer the Body Write. Is it running the MetroDom brain firmware? Or the reply got lost.");
            return 1;
        }

        if (reply.Status != BodyWriteStatus.Written)
        {
            Ui.Fail($"{port.Name}: {reply.Describe()}.");
            return 1;
        }

        Ui.Success($"{port.Name}: {reply.Describe()}.");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < ComeBack && !cancellationToken.IsCancellationRequested)
        {
            Thread.Sleep(250);
            var output = backend.Outputs().FirstOrDefault(p => p.Name == port.Name || p.Name == body.Name);
            if (output is null)
            {
                continue; // still restarting, or it came back under its new USB name
            }

            var now = Identifier.Identify(backend, Identifier.DefaultTimeout, [output])[0];
            if (now.Profile?.Name == body.Name)
            {
                Ui.Success($"{output.Name} now answers as {body.Name} (notes {string.Join(",", now.Profile.Notes!.Order())}).");
                var (answered, back) = BodyWriter.Read(backend, output, BodyWriter.DefaultTimeout);
                if (answered && back is not null && !back.AsSpan().SequenceEqual(record))
                {
                    Ui.Warn($"{output.Name} booted from a record that differs from the one written. Check it with metrodom body read.");
                    return 1;
                }

                if (answered && back is not null)
                {
                    Ui.Success("It booted from exactly the record written.");
                }

                return 0;
            }
        }

        Ui.Warn($"The brain didn't answer as {body.Name} within {ComeBack.TotalSeconds:0} s. Check with metrodom devices --identify.");
        return 0;
    }

    /// <summary>
    /// The body's EEPROM only takes a write while its button is held (WP to ground). A brain gets
    /// the prompt; a virtual instrument has no button. Piped: say so and go, the person is there.
    /// </summary>
    private static bool AskForTheButton(IdentifiedPort port, CancellationToken cancellationToken)
    {
        if (port.Identity?.Model == DeviceIdentity.VirtualModel)
        {
            Ui.Note("A virtual instrument: no write button to hold.");
            return true;
        }

        if (!Ui.IsInteractive)
        {
            Ui.Note("Hold the body's write button now; the write takes about a second.");
            return true;
        }

        AnsiConsole.MarkupLine("[bold]Hold the body's write button[/], then press [bold]Enter[/] (keep holding until it says written). Esc cancels.");
        while (!cancellationToken.IsCancellationRequested)
        {
            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.Enter:
                    return true;
                case ConsoleKey.Escape:
                    Ui.Note("Cancelled; nothing was sent.");
                    return false;
            }
        }

        return false;
    }
}
