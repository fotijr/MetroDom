using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Arranging;
using MetroDom.Core.Timing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Plays a song to one or more outputs through the latency-compensating scheduler (ADR 0004).
/// A human sets the tempo: <c>--tempo</c> at the start, tap / nudge keys while it plays.
/// </summary>
internal sealed class PlayCommand : Command<PlayCommand.Settings>
{
    private const double Nudge = 1.02;

    /// <summary>Lets the loop thread start before the first note, so beat one isn't late.</summary>
    private static readonly TimeSpan Preroll = TimeSpan.FromMilliseconds(250);

    public sealed class Settings : OutputSettings
    {
        [CommandArgument(0, "<SONG>")]
        [Description("Standard MIDI File to play (.mid).")]
        public string Path { get; init; } = "";

        [CommandOption("-a|--arrange")]
        [Description("Let the arranger give each output the part it plays best, on its first channel, using its /NOTES (transposed, folded, thinned to fit). Without it, outputs get their channels as written in the file.")]
        public bool Arrange { get; init; }

        [CommandOption("--tap <PORT>")]
        [Description("MIDI input that taps the tempo, e.g. a foot pedal or pad (index, name or part of a name from [bold]metrodom devices[/]). Works alongside the Space key.")]
        public string? Tap { get; init; }

        [CommandOption("--tap-on <TRIGGER>")]
        [Description("Which messages from [bold]--tap[/] count: [bold]any[/] (default: Note On or a pedal press), [bold]note[/][:N], [bold]cc[/][:N] (press = value ≥ 64), [bold]pc[/][:N], optionally @CHANNEL (e.g. [bold]cc:64[/], [bold]note:36@10[/]).")]
        public string? TapOn { get; init; }

        [CommandOption("--clock-in <PORT>")]
        [Description("Follow the tempo of MIDI Clock from this input (a drum machine, a DAW). When the clock stops, the band holds its tempo.")]
        public string? ClockIn { get; init; }

        public TapTrigger Trigger => TapTrigger.TryParse(TapOn, out var trigger, out _) ? trigger : new TapTrigger();

        [CommandOption("-s|--seat <ROLE>")]
        [Description(SeatList.Help)]
        public string[]? Seat { get; init; }

        public IReadOnlyList<Core.Arranging.Seat> Seats => SeatList.TryParse(Seat, out var seats, out _) ? seats : [];

        [CommandOption("--count-in <BARS>")]
        [Description("Count in this many bars (1-8) on a real instrument before the song, first beat of each bar accented. See [bold]--click[/].")]
        [DefaultValue(0)]
        public int CountInBars { get; init; }

        [CommandOption("--click <CHANNEL/NOTE>")]
        [Description("What plays the count-in: a channel (1-16) and note, e.g. [bold]10/76[/] (default: woodblock on the drums channel) or [bold]1/C6[/]. The output on that channel strikes it, through the safety governor.")]
        public string? Click { get; init; }

        public (int Channel, int Note) ClickNote => TryParseClick(Click, out var click, out _) ? click : (9, 76);

        [CommandOption("-t|--tempo <BPM>")]
        [Description("Start at this tempo instead of the file's. Tempo changes in the file scale with it.")]
        public double? Tempo { get; init; }

        public override ValidationResult Validate()
        {
            if (!File.Exists(Path))
            {
                return ValidationResult.Error($"file not found: {Path}");
            }

            if (!SeatList.TryParse(Seat, out _, out var seatError))
            {
                return ValidationResult.Error($"--seat: {seatError}");
            }

            if (Tempo is not (null or (>= TapTempo.MinBpm and <= TapTempo.MaxBpm)))
            {
                return ValidationResult.Error($"--tempo must be {TapTempo.MinBpm:0}-{TapTempo.MaxBpm:0} BPM.");
            }

            if (CountInBars is < 0 or > 8)
            {
                return ValidationResult.Error("--count-in must be 0-8 bars.");
            }

            if (!TryParseClick(Click, out _, out var clickError))
            {
                return ValidationResult.Error($"--click: {clickError}");
            }

            if (!TapTrigger.TryParse(TapOn, out _, out var error))
            {
                return ValidationResult.Error($"--tap-on: {error}");
            }

            if (TapOn is not null && Tap is null)
            {
                return ValidationResult.Error("--tap-on needs --tap <PORT>.");
            }

            return Tap is not null && ClockIn is not null
                ? ValidationResult.Error("--tap and --clock-in both set the tempo; pick one.")
                : base.Validate();
        }
    }

    /// <summary>Parses <c>CHANNEL/NOTE</c> (channel 1-16, note number or name). Null means the default.</summary>
    internal static bool TryParseClick(string? raw, out (int Channel, int Note) click, out string? error)
    {
        click = (9, 76);
        error = null;
        if (raw is null)
        {
            return true;
        }

        var parts = raw.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var channel) || channel is < 1 or > 16
            || !NoteList.TryParse(parts[1], out var notes, out _) || notes.Count != 1)
        {
            error = $"'{raw}' is not CHANNEL/NOTE (e.g. 10/76 or 1/C6).";
            return false;
        }

        click = (channel - 1, notes.First());
        return true;
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var roles = SongRoles.Load(settings.Path);
        var midiFile = SeatList.Apply(MidiFile.Read(settings.Path), settings.Seats, roles);
        var tempoMap = midiFile.GetTempoMap();
        var song = System.IO.Path.GetFileName(settings.Path);
        var fileBpm = FileBpm(tempoMap, TimeSpan.Zero);
        var startSpeed = settings.Tempo is { } bpm ? Clamp(bpm / fileBpm) : 1;

        string output;
        string[] heldBack;
        using (var session = new Session(settings))
        {
            output = session.OutputName;
            var clock = new SystemClock();
            var scheduler = new LatencyScheduler(session.Orchestra, clock, session.Profiles);
            scheduler.Sent += s => session.Activity.Record(s.Event);
            var timeline = SongTimeline.From(midiFile);
            if (settings.Arrange)
            {
                var band = session.Outputs.Select(o => new BandMember(o.Profile, o.Channels[0])).ToArray();
                var arrangement = Arranger.Arrange(midiFile, band, new ArrangerOptions { Roles = roles });
                ArrangementView.Write(arrangement, band);
                timeline = arrangement.Timeline;
            }

            var (beat, beatsPerBar) = CountIn.Meter(midiFile);
            var (clickChannel, clickNote) = settings.ClickNote;
            (timeline, var countIn) = CountIn.Prepend(timeline, settings.CountInBars, beat, beatsPerBar, clickChannel, clickNote);
            var sections = new SectionNavigator(scheduler, SongSections.Shift(SongSections.From(midiFile), countIn));
            scheduler.Jumped += (_, to) => session.Messages.Add(
                sections.Sections.FirstOrDefault(s => s.Start == to) is { } target ? $"→ {target.Name}" : "→ jump");

            // Start from silence. Also warms up every output's send path, so the first note isn't late.
            session.Orchestra.Panic();
            scheduler.Start(timeline, startSpeed, Preroll);
            TimeSpan SongPosition() => scheduler.Position > countIn ? scheduler.Position - countIn : TimeSpan.Zero;

            using var loop = new SchedulerLoop(scheduler, clock);
            loop.Finished += session.RequestStop;
            loop.Failed += ex =>
            {
                session.Messages.Add($"Scheduler stopped: {ex.Message}");
                session.RequestStop();
            };

            var tap = new TapTempo();
            var tapGate = new Lock(); // Keys (UI thread) and the pedal (input thread) share the tap history.
            string? tempoSource = null;
            void SetTempo(double bpm, string source)
            {
                scheduler.SetSpeed(Clamp(bpm / FileBpm(tempoMap, SongPosition())));
                Volatile.Write(ref tempoSource, source);
                loop.Wake();
            }

            void Tapped(string source)
            {
                double? bpm;
                lock (tapGate)
                {
                    bpm = tap.Tap(clock.Now);
                }

                if (bpm is { } tapped)
                {
                    SetTempo(tapped, source);
                }
            }

            using var tapInput = settings.Tap is null ? null : TapInput(settings, Tapped);
            using var clockInput = settings.ClockIn is null ? null : ClockInput(settings, clock, bpm => SetTempo(bpm, "MIDI Clock"));

            if (!Ui.IsInteractive)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Playing {song} to {output} at {fileBpm * startSpeed:0.#} BPM, lookahead {scheduler.Lookahead.TotalMilliseconds:0.#} ms{Muted(settings)}{CountInNote(settings)}{TempoFrom(tapInput, clockInput)}. Ctrl+C to stop."));
            }

            loop.Start();
            LiveStatus.Run(
                session,
                () => LiveStatus.Panel(
                    $"▶ {Markup.Escape(song)}",
                    session,
                    LiveStatus.Progress(scheduler.Position, scheduler.Duration),
                    sections.Describe() is { } where
                        ? new Rows(TempoLine(scheduler, FileBpm(tempoMap, SongPosition()), Volatile.Read(ref tempoSource)), new Markup($"[grey]Section[/] {Markup.Escape(where)}"))
                        : TempoLine(scheduler, FileBpm(tempoMap, SongPosition()), Volatile.Read(ref tempoSource)),
                    "Space tap · +/- tempo · 0 file tempo" + (sections.Sections.Count > 0 ? " · V vamp · O outro" : "") + TempoFrom(tapInput, clockInput)),
                key =>
                {
                    switch (key.KeyChar)
                    {
                        case ' ' or 't' or 'T':
                            Tapped("tapped");
                            break;
                        case '+' or '=':
                            scheduler.SetSpeed(Clamp(scheduler.Speed * Nudge));
                            Volatile.Write(ref tempoSource, "nudged");
                            loop.Wake();
                            break;
                        case '-' or '_':
                            scheduler.SetSpeed(Clamp(scheduler.Speed / Nudge));
                            Volatile.Write(ref tempoSource, "nudged");
                            loop.Wake();
                            break;
                        case 'v' or 'V':
                            session.Messages.Add(sections.ToggleVamp());
                            loop.Wake();
                            break;
                        case 'o' or 'O':
                            session.Messages.Add(sections.JumpToOutro());
                            loop.Wake();
                            break;
                        case '0':
                            scheduler.SetSpeed(1);
                            Volatile.Write(ref tempoSource, null);
                            loop.Wake();
                            break;
                    }
                });
            heldBack = [.. session.SafetySummary()];
        }

        foreach (var line in heldBack)
        {
            Ui.Warn($"Safety governor, {line}.");
        }

        Ui.Success($"Stopped. Panic sent to {output}.");
        return 0;
    }

    /// <summary>The file's tempo at a song position (tempo maps can change mid-song).</summary>
    private static double FileBpm(TempoMap tempoMap, TimeSpan position) =>
        tempoMap.GetTempoAtTime(new MetricTimeSpan(position)).BeatsPerMinute;

    private static double Clamp(double speed) => Math.Clamp(speed, LatencyScheduler.MinSpeed, LatencyScheduler.MaxSpeed);

    private static Markup TempoLine(LatencyScheduler scheduler, double fileBpm, string? source) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"[grey]Tempo[/]   [bold]{fileBpm * scheduler.Speed:0.#} BPM[/] [grey](file {fileBpm:0.#} × {scheduler.Speed:0.00}{(source is null ? "" : ", " + Markup.Escape(source))}) · lookahead {scheduler.Lookahead.TotalMilliseconds:0.#} ms[/]"));

    /// <summary>Opens <c>--tap</c>: every message matching <c>--tap-on</c> is a tap.</summary>
    internal static IMidiInputPort TapInput(Settings settings, Action<string> tapped)
    {
        var input = PortPicker.Input(settings.Tap, "--tap");
        Listen(input, settings.Trigger, () => tapped("pedal"));
        input.Start();
        return input;
    }

    /// <summary>Feeds <paramref name="input"/>'s taps to <paramref name="tapped"/>. Runs on the input thread.</summary>
    internal static void Listen(IMidiInputPort input, TapTrigger trigger, Action tapped)
    {
        var gate = new Lock();
        input.EventReceived += e =>
        {
            bool isTap;
            lock (gate)
            {
                isTap = trigger.IsTap(e);
            }

            if (isTap)
            {
                tapped();
            }
        };
    }

    /// <summary>Opens <c>--clock-in</c> and reports tempo changes worth following.</summary>
    internal static IMidiInputPort ClockInput(Settings settings, IClock clock, Action<double> tempo)
    {
        var input = PortPicker.Input(settings.ClockIn, "--clock-in");
        Follow(input, new ClockTempo(), clock, tempo);
        input.Start();
        return input;
    }

    /// <summary>Feeds <paramref name="input"/>'s MIDI Clock to <paramref name="follower"/>. Runs on the input thread.</summary>
    internal static void Follow(IMidiInputPort input, ClockTempo follower, IClock clock, Action<double> tempo)
    {
        var gate = new Lock();
        input.EventReceived += e =>
        {
            double? bpm = null;
            lock (gate)
            {
                switch (e)
                {
                    case TimingClockEvent:
                        bpm = follower.Tick(clock.Now);
                        break;
                    case StopEvent:
                        follower.Reset();
                        break;
                }
            }

            if (bpm is { } changed)
            {
                tempo(changed);
            }
        };
    }

    private static string CountInNote(Settings settings) =>
        settings.CountInBars == 0 ? "" : string.Create(
            CultureInfo.InvariantCulture,
            $", {settings.CountInBars}-bar count-in on ch {settings.ClickNote.Channel + 1} note {settings.ClickNote.Note}");

    private static string TempoFrom(IMidiInputPort? tap, IMidiInputPort? clock) =>
        tap is not null ? $" · tap from {tap.Name}" : clock is not null ? $" · tempo from {clock.Name} (MIDI Clock)" : "";

    private static string Muted(OutputSettings settings) =>
        settings.MutedChannels.Count > 0 ? $" (muted: {ChannelList.Format(settings.MutedChannels)})" : "";
}
