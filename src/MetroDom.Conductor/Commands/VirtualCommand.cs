using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.MusicTheory;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Bodies;
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
        [Description("A virtual instrument, NAME[[=CHANNELS]]@LATENCY_MS[[/NOTES]], repeatable (e.g. [bold]-i xylo=1@10 -i drums=10@35[/]). It only listens on its channels. Default: xylo on every channel but 10 at 10 ms, drums on 10 at 35 ms.")]
        public string[]? Instruments { get; init; }

        [CommandOption("-b|--body <FILE>")]
        [Description("A virtual brain running this body file, repeatable: a port with the body's name that strikes, refuses (busy, interval, duty, polyphony) and flashes exactly as a real brain would. Without [bold]-i[/], only the bodies.")]
        public string[]? Bodies { get; init; }

        [CommandOption("--onsets")]
        [Description("Also create a [bold]MetroDom onsets[/] port that sends a Note On whenever a virtual instrument would be heard, like a piezo trigger or mic would: point [bold]calibrate --onsets[/] or [bold]play --onsets[/] at it.")]
        public bool Onsets { get; init; }

        [CommandOption("--drift <MS>")]
        [Description("Strike this much later (or earlier, negative) than each instrument's profile says, to try the drift check. Default 0.")]
        public double Drift { get; init; }

        [CommandOption("--jitter <MS>")]
        [Description("Each strike lands up to ± this much off its latency (random). Default 0.")]
        public double Jitter { get; init; }

        public IReadOnlyList<InstrumentSpec> Band =>
            InstrumentSpec.TryParseAll(Instruments, out var specs, out _) && specs.Count > 0 ? specs
            : Bodies is { Length: > 0 } ? []
            : InstrumentSpec.DefaultBand;

        public override ValidationResult Validate()
        {
            if (!InstrumentSpec.TryParseAll(Instruments, out _, out var error))
            {
                return ValidationResult.Error($"--instrument: {error}");
            }

            if (Jitter is < 0 or > 100)
            {
                return ValidationResult.Error("--jitter must be 0-100 ms.");
            }

            return Drift is < -100 or > 500 ? ValidationResult.Error("--drift must be -100 to 500 ms.") : ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var bodies = (settings.Bodies ?? []).Select(BodyFile.Load).ToArray();
        var clock = new SystemClock();
        var console = new Lock();
        var ports = new List<IVirtualPort>();
        IVirtualPort? onsetPort = null;
        OnsetEmitter? emitter = null;
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
            if (settings.Onsets)
            {
                onsetPort = MidiDevices.CreateVirtualInput(OnsetsPort);
                onsetPort.Start();
                emitter = new OnsetEmitter(onsetPort, clock);
            }

            var drift = TimeSpan.FromMilliseconds(settings.Drift);
            var seed = 0;
            foreach (var spec in settings.Band)
            {
                var port = MidiDevices.CreateVirtualInput($"{PortPrefix} {spec.Name}");
                ports.Add(port);
                var declared = spec.ToProfile();

                // --drift: it plays late (or early) but still declares the old latency, like a worn mechanism.
                var actual = declared with
                {
                    Latency = Max0(declared.Latency + drift),
                    NoteLatency = declared.NoteLatency.ToDictionary(kv => kv.Key, kv => Max0(kv.Value + drift)),
                };
                var instrument = new VirtualInstrument(actual, clock, TimeSpan.FromMilliseconds(settings.Jitter), seed++);
                if (emitter is not null)
                {
                    instrument.Struck += (_, s) => emitter.Emit(s);
                }

                Wire(port, spec, declared, instrument, clock, console);
                port.Start();
            }

            var ready = settings.Band.Zip(ports).Select(p => string.Create(CultureInfo.InvariantCulture,
                $"  [green]●[/] {Markup.Escape(p.Second.Name)}  [grey]channels {ChannelList.Format(p.First.Channels)}, latency {p.First.Latency.TotalMilliseconds:0.#} ms[/]")).ToList();
            foreach (var body in bodies)
            {
                // A brain's USB name is its body's name, so this port is named exactly as the real one will be.
                var port = MidiDevices.CreateVirtualInput(body.Name);
                ports.Add(port);
                var instrument = VirtualInstrument.ForBody(body, clock, TimeSpan.FromMilliseconds(settings.Jitter), seed++);
                if (emitter is not null)
                {
                    instrument.Struck += (_, s) => emitter.Emit(s);
                }

                var label = body.Name.StartsWith(PortPrefix + " ", StringComparison.Ordinal) ? body.Name[(PortPrefix.Length + 1)..] : body.Name;
                Wire(port, new InstrumentSpec(label, InstrumentSpec.AllChannels, instrument.Profile.MaxLatency), instrument.Profile, instrument, clock, console);
                port.Start();
                ready.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  [green]●[/] {Markup.Escape(port.Name)}  [grey]brain running {Markup.Escape(body.Id)}: {body.Notes.Count} note(s), {(body.Channel is { } c ? $"channel {c + 1}" : "any channel")}, polyphony {body.Polyphony}, latency {(body.Notes.Max(n => n.LatencyMs) is var l and > 0 ? $"{l} ms" : "not measured")}[/]"));
            }

            AnsiConsole.MarkupLine("[bold]Virtual instruments ready[/] [grey](send to them like any MIDI output)[/]");
            foreach (var line in ready)
            {
                AnsiConsole.MarkupLine(line);
            }

            if (onsetPort is not null)
            {
                AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                    $"  [green]●[/] {OnsetsPort}  [grey]a Note On per strike, when it would be heard{(settings.Drift != 0 ? $" (drift {settings.Drift:+0.#;-0.#} ms)" : "")}[/]"));
                AnsiConsole.MarkupLine($"[grey]Try:[/] metrodom calibrate -o \"{Markup.Escape(ports[0].Name)}\" --onsets \"{OnsetsPort}\"");
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

            emitter?.Dispose();
            onsetPort?.Dispose();
        }

        Ui.Success("Virtual instruments removed.");
        return 0;
    }

    /// <summary>The port <c>--onsets</c> creates.</summary>
    public const string OnsetsPort = PortPrefix + " onsets";

    private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private static void Wire(IVirtualPort port, InstrumentSpec spec, InstrumentProfile declared, VirtualInstrument instrument, SystemClock clock, Lock console)
    {
        var channels = spec.Channels.ToHashSet();
        var profileJson = InstrumentProfileJson.Write(declared);
        var bodies = new BodyReceiver();
        TimeSpan? lastPanic = null;
        instrument.Struck += (_, s) => Print(console, s.ReceivedAt, spec.Name, string.Create(
            CultureInfo.InvariantCulture,
            $"ch {s.Channel + 1,2}  {NoteName(s.Note),-4} [grey]({s.Note,3})[/]  vel {s.Velocity,3}{Pulse(s)}  [grey]→ heard at[/] {s.SoundsAt.TotalSeconds:0.000} [grey](+{(s.SoundsAt - s.ReceivedAt).TotalMilliseconds:0.#} ms)[/]"));
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

            if (BodyWrite.IsReadRequest(e))
            {
                foreach (var chunk in BodyWrite.ReadReply(bodies.Record))
                {
                    port.Send(chunk); // nothing written yet: "no record", like a bench brain
                }

                Print(console, clock.Now, spec.Name, "[grey]body read: sent its body record[/]");
                return;
            }

            if (BodyWrite.IsChunk(e))
            {
                // Like a brain: check the record and answer. It then describes itself as that body
                // (it still plays as its -i spec), so `metrodom body write` can be tried end to end.
                if (bodies.Add(e, out var written) is { } answer)
                {
                    if (written is not null)
                    {
                        profileJson = InstrumentProfileJson.Write(written.ToProfile(), written.Id);
                        Print(console, clock.Now, spec.Name, $"[grey]body write: now describes itself as {Markup.Escape(written.Name)}[/]");
                    }
                    else
                    {
                        Print(console, clock.Now, spec.Name, "[yellow]body write: refused[/]");
                    }

                    port.Send(answer);
                }

                return;
            }

            if (ProfileExchange.IsAllOff(e))
            {
                instrument.Send(e);
                lastPanic = clock.Now; // the CC burst that follows is the same panic
                Print(console, clock.Now, spec.Name, "[red]panic: all off[/] [grey](MetroDom SysEx)[/]");
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

    /// <summary>"  26 ms ■" for a brain-modeled strike: its pulse, and the LED flash in its own color.</summary>
    private static string Pulse(Strike s) =>
        s.PulseMs == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $"  {s.PulseMs,3} ms{(s.Flash is { } c ? $" [#{c:X6}]■[/]" : "")}");

    private static string NoteName(int number) => Note.Get((SevenBitNumber)(byte)number).ToString();
}

/// <summary>
/// Sends a Note On on the onsets port at the moment each strike would be heard, as a piezo
/// trigger or a mic-to-MIDI box would. One thread, sleeping until close and spinning the last
/// millisecond, so onsets land within about a millisecond of when they're due.
/// </summary>
internal sealed class OnsetEmitter : IDisposable
{
    private readonly IVirtualPort _port;
    private readonly IClock _clock;
    private readonly PriorityQueue<Strike, TimeSpan> _due = new();
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;

    public OnsetEmitter(IVirtualPort port, IClock clock)
    {
        _port = port;
        _clock = clock;
        _thread = new Thread(Run) { IsBackground = true, Name = "MetroDom onsets", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Emit(Strike strike)
    {
        lock (_gate)
        {
            _due.Enqueue(strike, strike.SoundsAt);
        }

        _wake.Set();
    }

    private void Run()
    {
        while (!_stopping)
        {
            Strike? next = null;
            TimeSpan wait;
            lock (_gate)
            {
                wait = _due.TryPeek(out var strike, out var at) ? at - _clock.Now : Timeout.InfiniteTimeSpan;
                if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero)
                {
                    next = _due.Dequeue();
                }
            }

            if (next is not null)
            {
                _port.Send(new NoteOnEvent((SevenBitNumber)next.Note, (SevenBitNumber)Math.Clamp(next.Velocity, 1, 127)) { Channel = (FourBitNumber)next.Channel });
                _port.Send(new NoteOffEvent((SevenBitNumber)next.Note, SevenBitNumber.MinValue) { Channel = (FourBitNumber)next.Channel });
            }
            else if (wait == Timeout.InfiniteTimeSpan || wait > TimeSpan.FromMilliseconds(2))
            {
                _wake.WaitOne(wait == Timeout.InfiniteTimeSpan ? wait : wait - TimeSpan.FromMilliseconds(1));
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        _wake.Dispose();
    }
}
