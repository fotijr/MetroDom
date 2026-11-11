using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.MusicTheory;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Discovery;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Stands up virtual instruments as real OS MIDI ports, so <c>metrodom play</c>, a DAW or a
/// keyboard can play the band before any hardware exists. Each one behaves like the generic
/// firmware (strike on Note On, drops what it can't play) and prints what it would play and when.
/// </summary>
internal sealed class VirtualCommand : Command<VirtualCommand.Settings>
{
    /// <summary>Prefix for port names, so virtual instruments are easy to spot in <c>metrodom devices</c>.</summary>
    public const string PortPrefix = "MetroDom";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-i|--instrument <SPEC>")]
        [Description("A virtual instrument, NAME[=CHANNELS]@LATENCY_MS[/NOTES], repeatable (e.g. [bold]-i xylo=1@10 -i drums=10@35[/]). It only listens on its channels. Default: xylo on every channel but 10 at 10 ms, drums on 10 at 35 ms.")]
        public string[]? Instruments { get; init; }

        public IReadOnlyList<InstrumentSpec> Band =>
            InstrumentSpec.TryParseAll(Instruments, out var specs, out _) && specs.Count > 0 ? specs : InstrumentSpec.DefaultBand;

        public override ValidationResult Validate() =>
            InstrumentSpec.TryParseAll(Instruments, out _, out var error)
                ? ValidationResult.Success()
                : ValidationResult.Error($"--instrument: {error}");
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clock = new SystemClock();
        var console = new Lock();
        var ports = new List<IVirtualPort>();
        using var stop = new ManualResetEventSlim();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stop.Set();
        }

        Console.CancelKeyPress += OnCancel;
        using var registration = cancellationToken.Register(stop.Set);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, e =>
        {
            e.Cancel = true; // Headless (systemd, docker stop): remove the ports cleanly, like Ctrl+C.
            stop.Set();
        });
        try
        {
            foreach (var spec in settings.Band)
            {
                var port = MidiDevices.CreateVirtualInput($"{PortPrefix} {spec.Name}");
                ports.Add(port);
                Wire(port, spec, new VirtualInstrument(spec.ToProfile(), clock), clock, console);
                port.Start();
            }

            AnsiConsole.MarkupLine("[bold]Virtual instruments ready[/] [grey](send to them like any MIDI output)[/]");
            foreach (var (spec, port) in settings.Band.Zip(ports))
            {
                AnsiConsole.MarkupLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [green]●[/] {Markup.Escape(port.Name)}  [grey]channels {ChannelList.Format(spec.Channels)}, latency {spec.Latency.TotalMilliseconds:0.#} ms[/]"));
            }

            AnsiConsole.MarkupLine($"[grey]Try:[/] metrodom play song.mid -o \"{Markup.Escape(ports[0].Name)}\"   [grey]Ctrl+C to stop.[/]");
            stop.Wait(CancellationToken.None);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            foreach (var port in ports)
            {
                port.Dispose();
            }
        }

        Ui.Success("Virtual instruments removed.");
        return 0;
    }

    private static void Wire(IVirtualPort port, InstrumentSpec spec, VirtualInstrument instrument, SystemClock clock, Lock console)
    {
        var channels = spec.Channels.ToHashSet();
        var profileJson = InstrumentProfileJson.Write(instrument.Profile);
        TimeSpan? lastPanic = null;
        instrument.Struck += (_, s) => Print(console, s.ReceivedAt, spec.Name, string.Create(
            CultureInfo.InvariantCulture,
            $"ch {s.Channel + 1,2}  {NoteName(s.Note),-4} [grey]({s.Note,3})[/]  vel {s.Velocity,3}  [grey]→ heard at[/] {s.SoundsAt.TotalSeconds:0.000} [grey](+{(s.SoundsAt - s.ReceivedAt).TotalMilliseconds:0.#} ms)[/]"));
        instrument.Rejected += (_, note, reason) => Print(console, clock.Now, spec.Name, $"[yellow]skipped {NoteName(note)} ({note}): {Markup.Escape(reason)}[/]");

        port.EventReceived += e =>
        {
            if (Identity.ReplyTo(e) is { } reply)
            {
                port.Send(reply); // ADR 0007: answer "who are you?" like a MetroDom instrument.
                Print(console, clock.Now, spec.Name, "[grey]identity request: answered as a MetroDom virtual instrument[/]");
                return;
            }

            if (ProfileExchange.IsRequest(e))
            {
                foreach (var chunk in ProfileExchange.Reply(profileJson))
                {
                    port.Send(chunk); // ADR 0007 step 3: describe ourselves.
                }

                Print(console, clock.Now, spec.Name, "[grey]profile request: sent its profile[/]");
                return;
            }

            if (e is not ChannelEvent channelEvent || !channels.Contains(channelEvent.Channel))
            {
                return;
            }

            instrument.Send(e);
            if (e is ControlChangeEvent cc && (cc.ControlNumber == ControlNumbers.AllSoundOff || cc.ControlNumber == ControlNumbers.AllNotesOff)
                && (lastPanic is null || clock.Now - lastPanic > TimeSpan.FromMilliseconds(250)))
            {
                lastPanic = clock.Now; // A panic is 32 messages; report the burst once.
                Print(console, clock.Now, spec.Name, "[red]panic: all off[/]");
            }
        };
    }

    /// <summary>What every virtual instrument says it is: MetroDom, model 0 (software), the Conductor's version.</summary>
    internal static readonly DeviceIdentity Identity = DeviceIdentity.MetroDom(
        DeviceIdentity.VirtualModel,
        typeof(VirtualCommand).Assembly.GetName().Version ?? new Version(0, 0));

    private static void Print(Lock console, TimeSpan at, string instrument, string markup)
    {
        lock (console)
        {
            AnsiConsole.MarkupLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[grey]{at.TotalSeconds,9:0.000}[/]  [bold]{Markup.Escape(instrument),-8}[/] {markup}"));
        }
    }

    private static string NoteName(int number) => Note.Get((SevenBitNumber)(byte)number).ToString();
}
