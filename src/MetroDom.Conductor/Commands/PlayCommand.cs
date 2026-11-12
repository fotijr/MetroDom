using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Conductor.Api;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Lighting;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Arranging;
using MetroDom.Core.Lighting;
using MetroDom.Core.Policy;
using MetroDom.Core.Setlist;
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
    private const double NudgeFactor = 1.02;

    /// <summary>Lets the loop thread start before the first note, so beat one isn't late.</summary>
    private static readonly TimeSpan Preroll = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the band re-aims at a followed player between hits.</summary>
    private static readonly TimeSpan FollowTick = TimeSpan.FromMilliseconds(50);

    public sealed class Settings : SetSettings
    {
        [CommandArgument(0, "<SONGS>")]
        [Description("Standard MIDI File(s) to play (.mid). Several make a setlist: each starts when the last ends, or on the start pedal when a controller maps one.")]
        public string[] Paths { get; init; } = [];

        public override ValidationResult Validate()
        {
            if (Paths.Length == 0)
            {
                return ValidationResult.Error("give at least one SONG.");
            }

            return Paths.FirstOrDefault(p => !File.Exists(p)) is { } missing
                ? ValidationResult.Error($"file not found: {missing}")
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
        var queue = new SetQueue();
        queue.Replace(settings.Paths.Select(p => (p, System.IO.Path.GetFileName(p))));
        return RunSet(settings, new SetOptions(queue, song => song.Song, KeepRunning: false, (live, session) => StartApi(settings, live, session)));
    }

    /// <summary>Runs a set until its queue is done (or, for <c>serve</c>, until Ctrl+C).</summary>
    internal static int RunSet(SetSettings settings, SetOptions options)
    {
        var policy = settings.PolicyProfile;
        var queue = options.Queue;
        string output;
        string[] heldBack;
        using (var session = new Session(settings))
        {
            if (!Ui.IsInteractive)
            {
                // No live panel to show them in: logs and scripts get plain lines (hot-plug, vamp, panic).
                session.Messages.Added += Console.WriteLine;
            }

            using var live = new LiveControl(session, new SystemClock(), queue);
            using var tapInput = settings.Tap is null ? null : TapInput(settings, live.Tap);
            using var clockInput = settings.ClockIn is null ? null : ClockInput(settings, live.Clock, live.FollowClock);
            using var followInput = settings.Follow is null ? null : FollowInput(settings, live.Beat);
            using var controlInput = settings.Control is null ? null : ControlInput(settings.Control, policy, live.Control);
            var startPedal = controlInput is not null && policy.Controls.ContainsKey(ControlAction.StartStop);
            var following = followInput is not null;
            var canRestart = options.KeepRunning || startPedal || following || Ui.IsInteractive;

            // A service always waits for start before its first song, even with autoplay: a box
            // that just booted (or rebooted) never starts the band by itself.
            var wait = startPedal || following || options.KeepRunning;
            var inputs = TempoFrom(tapInput, clockInput, followInput) + (controlInput is null ? "" : $" · controls from {controlInput.Name}");
            using var api = options.StartApi(live, session);
            using var lights = StartLights(settings, session, live);
            using var padInput = lights is null || settings.LightsPad is null ? null : PadInput(settings.LightsPad, lights);
            if (lights is not null)
            {
                inputs += $" · lights {lights.Profile.Name}" + (padInput is null ? "" : $" (cues from {padInput.Name})");
            }

            session.WatchForDevices(MidiDevices.Backend.Value);
            if (api is ApiHandle handle)
            {
                inputs += $" · API {handle.Address}";
            }

            live.Cue(0);
            var over = false;
            while (!over && !session.IsStopping)
            {
                if (queue.Now is not { } entry)
                {
                    if (!options.KeepRunning || !WaitForSongs(session, live))
                    {
                        break;
                    }

                    // Someone queued a song: a jukebox plays it, a band waits for start.
                    wait = startPedal || following || !policy.Queue.Autoplay;
                    continue;
                }

                var title = SongTitle(entry, queue.Snapshot(), options.KeepRunning);
                if (options.Resolve(entry) is not { } path || !File.Exists(path))
                {
                    session.Messages.Add($"Skipping {entry.Title}: it's no longer in the library.");
                    queue.Advance();
                    continue;
                }

                if (wait)
                {
                    live.Begin(null);
                    if (following)
                    {
                        live.ListenForBeat(settings.Tempo ?? FileBpm(MidiFile.Read(path).GetTempoMap(), TimeSpan.Zero));
                    }

                    var signal = WaitForStart(session, live, title, policy, controlInput, followInput, remote: api is not null);
                    if (signal is null)
                    {
                        break; // Ctrl+C
                    }

                    if (signal == LiveSignal.Next)
                    {
                        queue.Advance();
                        continue;
                    }
                }

                // Hot-plugged devices join here, between songs, so the scheduler plans for their latency.
                session.AdmitPending();
                var set = queue.Snapshot();
                var end = PlaySong(session, live, settings, path, title, inputs, lights, hasNext: options.KeepRunning || set.Current + 1 < set.Songs.Count);
                if (end is null)
                {
                    break; // Ctrl+C or the scheduler failed: stop the set.
                }

                switch (end)
                {
                    case LiveSignal.Panic:
                        wait = true; // Same song again, but only when someone says so.
                        over = !canRestart;

                        break;
                    case LiveSignal.Stopped:
                        wait = canRestart;
                        queue.Advance();
                        break;
                    case LiveSignal.Next:
                        wait = false;
                        queue.Advance();
                        break;
                    default:
                        wait = startPedal || following;
                        queue.Advance();
                        break;
                }
            }

            output = session.OutputName; // The band may have grown during the set.
            heldBack = [.. session.SafetySummary()];
        }

        foreach (var line in heldBack)
        {
            Ui.Warn($"Safety governor, {line}.");
        }

        Ui.Success($"Stopped. Panic sent to {output}.");
        return 0;
    }

    /// <summary>Holds while the queue is empty (serve): until a song is queued (true) or Ctrl+C (false).</summary>
    private static bool WaitForSongs(Session session, LiveControl live)
    {
        using var queued = new ManualResetEventSlim();
        void OnChanged()
        {
            if (!live.Queue.IsDry)
            {
                queued.Set();
            }
        }

        live.Queue.Changed += OnChanged;
        try
        {
            live.Begin(null);
            if (!live.Queue.IsDry)
            {
                return true;
            }

            if (!Ui.IsInteractive)
            {
                Console.WriteLine("The queue is empty. Waiting for songs.");
            }

            LiveStatus.Run(
                session,
                () => LiveStatus.Panel("⏹ The queue is empty", session, progress: null, new Markup("[grey]Add songs from the remote.[/]")),
                until: queued);
            return !session.IsStopping;
        }
        finally
        {
            live.Queue.Changed -= OnChanged;
        }
    }

    /// <summary>
    /// Starts the rig in reactive mode when <c>--lights</c> is given. Fixtures the profile doesn't
    /// assign are shared out across the band as it is at the start of the set.
    /// </summary>
    private static LightShow? StartLights(SetSettings settings, Session session, LiveControl live)
    {
        if (settings.LightProfile is not { } rig)
        {
            return null;
        }

        var output = new SacnOutput(new UdpSacnSocket(), rig.Output, $"MetroDom {rig.Name}", log: session.Messages.Add, start: false);
        var band = session.Outputs.Select(o => (IReadOnlyList<int>)[.. o.Channels.Where(c => c != rig.Channel)]).ToList();
        var limits = settings.PolicyProfile.Lights;
        var show = new LightShow(rig, output, live.Clock, band, limits);

        // The rig is one more output: every channel for reactive pulses, its own channel for cues,
        // which no instrument gets any more (a robot must never play a cue note).
        var lost = session.Reserve(rig.Channel);
        session.Orchestra.AssignAll(show);
        session.Panicked += show.Safe;
        if (lost.Count > 0)
        {
            Ui.Note($"Channel {rig.Channel + 1} is for lights now: {string.Join(", ", lost)} won't get it.");
        }

        var where = string.Join(", ", rig.Universes.Select(u => $"universe {u} → {output.DestinationFor(u)}"));
        var follows = string.Join(", ", show.Reactive.Following.Select(f => $"{f.Key} {(f.Value is null ? "all" : "ch " + ChannelList.Format(f.Value))}"));
        Ui.Note($"Lights: {rig.Name} on {where} (sACN). Reactive: {follows}. Policy {settings.PolicyProfile.Mode}: {limits.Describe()}.");
        return show;
    }

    /// <summary>Holds until start (pedal or Enter), next (skip) or Ctrl+C (null).</summary>
    private static LiveSignal? WaitForStart(Session session, LiveControl live, string song, PolicyProfile policy, IMidiInputPort? controls, IMidiInputPort? follow, bool remote)
    {
        var how = controls is not null && policy.Controls.TryGetValue(ControlAction.StartStop, out var trigger)
            ? $"start pedal ({trigger} on {controls.Name})"
            : "Enter";
        if (follow is not null)
        {
            how += $" or a count-in on {follow.Name}";
        }

        if (remote)
        {
            how += " or Start on the remote";
        }
        if (!Ui.IsInteractive)
        {
            Console.WriteLine($"Ready: {song}. Waiting for {how}.");
        }

        LiveStatus.Run(
            session,
            () => LiveStatus.Panel($"⏸ Ready: {Markup.Escape(song)}", session, progress: null, new Markup($"[grey]Press[/] {Markup.Escape(how)} [grey]to play.[/]"), "Enter start · N skip · Space tap the starting tempo"),
            key =>
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    live.End(LiveSignal.Start);
                }
                else if (char.ToLowerInvariant(key.KeyChar) == 'n')
                {
                    live.End(LiveSignal.Next);
                }
                else if (key.KeyChar == ' ')
                {
                    live.Tap("tapped");
                }
            },
            live.Done);
        return session.IsStopping ? null : live.Signal;
    }

    /// <summary>Plays one song of the set until it ends. Returns why, or null to stop the set.</summary>
    private static LiveSignal? PlaySong(Session session, LiveControl live, SetSettings settings, string path, string song, string inputs, LightShow? lights, bool hasNext)
    {
        var roles = SongRoles.Load(path);
        var midiFile = SeatList.Apply(MidiFile.Read(path), settings.Seats, roles);
        var tempoMap = midiFile.GetTempoMap();
        var fileBpm = FileBpm(tempoMap, TimeSpan.Zero);
        var pendingBpm = live.TakePendingBpm(out var pendingSource);
        var startBpm = pendingBpm ?? settings.Tempo;
        var startSpeed = startBpm is { } bpm ? Clamp(bpm / fileBpm) : 1;

        var clock = live.Clock;
        var profiles = session.Profiles;
        if (lights is not null)
        {
            // Sent early by the rig's latency like any instrument, so light lands with sound.
            profiles = new Dictionary<IMidiOutput, InstrumentProfile>(profiles) { [lights] = lights.Timing };
            lights.Resume();
        }

        var scheduler = new LatencyScheduler(session.Orchestra, clock, profiles);
        scheduler.Sent += s =>
        {
            if (!ReferenceEquals(s.Output, lights))
            {
                session.Activity.Record(s.Event);
            }
        };

        var timeline = SongTimeline.From(midiFile);
        if (settings.Arrange)
        {
            var band = session.Outputs.Select(o => new BandMember(o.Profile, o.Channels[0])).ToArray();
            var cueChannel = lights?.Profile.Channel;
            bool IsCue(MidiEvent e) => e is ChannelEvent c && c.Channel == cueChannel;
            var forBand = midiFile;
            if (cueChannel is not null)
            {
                // The lights track is the rig's, not a part to hand to a robot: keep it as written.
                forBand = midiFile.Clone();
                forBand.RemoveTimedEvents(e => IsCue(e.Event));
            }

            var arrangement = Arranger.Arrange(forBand, band, new ArrangerOptions { Roles = roles });
            ArrangementView.Write(arrangement, band);
            timeline = [.. arrangement.Timeline.Concat(timeline.Where(e => IsCue(e.Event))).OrderBy(e => e.At)];
        }

        var (beat, beatsPerBar) = CountIn.Meter(midiFile);
        var (clickChannel, clickNote) = settings.ClickNote;
        (timeline, var countIn) = CountIn.Prepend(timeline, settings.CountInBars, beat, beatsPerBar, clickChannel, clickNote);
        var sections = new SectionNavigator(scheduler, SongSections.Shift(SongSections.From(midiFile), countIn));
        var beats = settings.Follow is null ? [] : SongBeats.WithCountIn(SongBeats.From(midiFile), settings.CountInBars * beatsPerBar, beat);
        scheduler.Jumped += (_, to) => session.Messages.Add(
            sections.Sections.FirstOrDefault(s => s.Start == to) is { } target ? $"→ {target.Name}" : "→ jump");

        // Start from silence. Also warms up every output's send path, so the first note isn't late.
        session.Orchestra.Panic();
        var onTheBeat = live.StartSong(scheduler, timeline, beats, startSpeed, Preroll);
        using var follower = settings.Follow is null ? null : new Timer(_ => live.Tick(), null, FollowTick, FollowTick);
        TimeSpan SongPosition() => scheduler.Position > countIn ? scheduler.Position - countIn : TimeSpan.Zero;

        using var loop = new SchedulerLoop(scheduler, clock);
        loop.Finished += () => live.End(LiveSignal.Finished);
        loop.Failed += ex =>
        {
            session.Messages.Add($"Scheduler stopped: {ex.Message}");
            session.RequestStop();
        };

        live.TempoSource = pendingBpm is null ? null : pendingSource;
        void SetTempo(double bpm, string source)
        {
            scheduler.SetSpeed(Clamp(bpm / FileBpm(tempoMap, SongPosition())));
            live.TempoSource = source;
            loop.Wake();
        }

        void Nudge(double speed, string? source)
        {
            scheduler.SetSpeed(Clamp(speed));
            live.TempoSource = source;
            loop.Wake();
        }

        live.Begin(new ActiveSong(scheduler, sections, SetTempo, loop.Wake, () => FileBpm(tempoMap, SongPosition())));
        if (!Ui.IsInteractive)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Playing {song} to {session.OutputName} at {fileBpm * scheduler.Speed:0.#} BPM{(onTheBeat ? " on the player's beat" : "")}, lookahead {scheduler.Lookahead.TotalMilliseconds:0.#} ms{Muted(settings)}{CountInNote(settings)}{inputs}. Ctrl+C to stop."));
        }

        loop.Start();
        LiveStatus.Run(
            session,
            () => LiveStatus.Panel(
                $"▶ {Markup.Escape(song)}",
                session,
                LiveStatus.Progress(scheduler.Position, scheduler.Duration),
                sections.Describe() is { } where
                    ? new Rows(TempoLine(scheduler, FileBpm(tempoMap, SongPosition()), TempoSource(live)), new Markup($"[grey]Section[/] {Markup.Escape(where)}"))
                    : TempoLine(scheduler, FileBpm(tempoMap, SongPosition()), TempoSource(live)),
                "Space tap · +/- tempo · 0 file tempo" + (sections.Sections.Count > 0 ? " · V vamp · O outro" : "")
                    + (hasNext ? " · N next" : "") + " · Enter stop · Esc panic" + inputs),
            key =>
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    live.Control(ControlAction.StartStop);
                    return;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    live.Control(ControlAction.Panic);
                    return;
                }

                switch (char.ToLowerInvariant(key.KeyChar))
                {
                    case ' ' or 't':
                        live.Tap("tapped");
                        break;
                    case '+' or '=':
                        Nudge(scheduler.Speed * NudgeFactor, "nudged");
                        break;
                    case '-' or '_':
                        Nudge(scheduler.Speed / NudgeFactor, "nudged");
                        break;
                    case '0':
                        Nudge(1, null);
                        break;
                    case 'v':
                        live.Control(ControlAction.Vamp);
                        break;
                    case 'o':
                        live.Control(ControlAction.Outro);
                        break;
                    case 'n':
                        live.Control(ControlAction.Next);
                        break;
                }
            },
            live.Done);

        var signal = live.Signal; // Read before Begin clears it.
        live.Begin(null);
        scheduler.Stop(); // Whatever ended the song, nothing is left sounding.
        return session.IsStopping ? null : signal;
    }

    /// <summary>Starts <c>--api</c>, if asked, and says where and with which token.</summary>
    private static ApiHandle? StartApi(SetSettings settings, LiveControl live, Session session)
    {
        if (settings.ApiAt is not { } endpoint)
        {
            return null;
        }

        var fromEnv = Environment.GetEnvironmentVariable("METRODOM_API_TOKEN") is { Length: > 0 } set ? set : null;
        var token = fromEnv ?? ControlApi.NewToken();
        var policy = settings.PolicyProfile;
        var api = ControlApi.Start(endpoint, new TokenStore(token), policy, live, session);
        Ui.Note(fromEnv is null
            ? $"Control API at {api.Address} · owner token {token}"
            : $"Control API at {api.Address} · owner token from METRODOM_API_TOKEN");
        Ui.Note($"Remote: open {ControlApi.PairingUrl(api.Address, endpoint, fromEnv is null ? token : null)} on a phone or browser (owner). Hand out operator and guest links with POST {ControlApi.Prefix}/tokens (policy {policy.Mode}).");
        if (!endpoint.IsLoopback)
        {
            Ui.Warn("The Control API is open to the network over plain HTTP: anyone who sees the token can drive the band. Use it on a network you trust.");
        }

        return new ApiHandle(api);
    }

    /// <summary>Stops the API synchronously at the end of the set.</summary>
    internal sealed class ApiHandle(ControlApi api, params IDisposable?[] alongside) : IDisposable
    {
        public Uri Address => api.Address;

        public void Dispose()
        {
            foreach (var other in alongside)
            {
                other?.Dispose();
            }

            api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>"song.mid (2/5)" for a set of files; a service's queue keeps changing, so just the title there.</summary>
    private static string SongTitle(QueuedSong song, QueueSnapshot queue, bool service) =>
        service || queue.Songs.Count + queue.Dropped == 1
            ? song.Title
            : string.Create(CultureInfo.InvariantCulture, $"{song.Title} ({queue.Current + 1 + queue.Dropped}/{queue.Songs.Count + queue.Dropped})");

    /// <summary>Opens <c>--control</c>: every message goes through the policy's control map.</summary>
    internal static IMidiInputPort ControlInput(string port, PolicyProfile policy, Action<ControlAction> act)
    {
        var input = PortPicker.Input(port, "--control");
        Route(input, policy.NewControlMap(), act);
        input.Start();
        return input;
    }

    /// <summary>Feeds <paramref name="input"/> through <paramref name="map"/> into <paramref name="act"/>. Runs on the input thread.</summary>
    internal static void Route(IMidiInputPort input, ControlMap map, Action<ControlAction> act)
    {
        var gate = new Lock();
        input.EventReceived += e =>
        {
            IReadOnlyList<ControlAction> actions;
            lock (gate)
            {
                actions = map.Match(e);
            }

            foreach (var action in actions)
            {
                act(action);
            }
        };
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
    internal static IMidiInputPort TapInput(SetSettings settings, Action<string> tapped)
    {
        var input = PortPicker.Input(settings.Tap, "--tap");
        Listen(input, settings.Trigger, () => tapped("pedal"));
        input.Start();
        return input;
    }

    /// <summary>Opens <c>--lights-pad</c>: every note and controller is a light cue.</summary>
    internal static IMidiInputPort PadInput(string port, LightShow lights)
    {
        var input = PortPicker.Input(port, "--lights-pad");
        input.EventReceived += lights.Cue;
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
    internal static IMidiInputPort ClockInput(SetSettings settings, IClock clock, Action<double> tempo)
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

    private static string CountInNote(SetSettings settings) =>
        settings.CountInBars == 0 ? "" : string.Create(
            CultureInfo.InvariantCulture,
            $", {settings.CountInBars}-bar count-in on ch {settings.ClickNote.Channel + 1} note {settings.ClickNote.Note}");

    private static string TempoFrom(IMidiInputPort? tap, IMidiInputPort? clock, IMidiInputPort? follow) =>
        tap is not null ? $" · tap from {tap.Name}"
        : clock is not null ? $" · tempo from {clock.Name} (MIDI Clock)"
        : follow is not null ? $" · following {follow.Name}"
        : "";

    /// <summary>What sets the tempo, for the status line: "following, locked at 98.2" or the last tap/nudge.</summary>
    private static string? TempoSource(LiveControl live) => live.Following switch
    {
        { State: BeatState.Listening } => "following, listening",
        { State: var state, Bpm: { } bpm } => string.Create(CultureInfo.InvariantCulture, $"following, {state.ToString().ToLowerInvariant()} at {bpm:0.#}"),
        _ => live.TempoSource,
    };

    /// <summary>Opens <c>--follow</c>: every message matching <c>--follow-on</c> is a hit.</summary>
    internal static IMidiInputPort FollowInput(SetSettings settings, Action hit)
    {
        var input = PortPicker.Input(settings.Follow, "--follow");
        Listen(input, settings.FollowTrigger, hit);
        input.Start();
        return input;
    }

    private static string Muted(OutputSettings settings) =>
        settings.MutedChannels.Count > 0 ? $" (muted: {ChannelList.Format(settings.MutedChannels)})" : "";
}

/// <summary>Everything about running a set, shared by <c>play</c> (a list of files) and <c>serve</c> (a library and a queue).</summary>
internal class SetSettings : OutputSettings
{
    [CommandOption("-c|--control <PORT>")]
    [Description("MIDI input of a foot controller (or any control surface) that drives the set: start/stop, next, tap, vamp, outro, panic, mapped by the policy (see [bold]--policy[/]). With start/stop mapped, each song waits for the pedal.")]
    public string? Control { get; init; }

    [CommandOption("-a|--arrange")]
    [Description("Let the arranger give each output the part it plays best, on its first channel, using its /NOTES (transposed, folded, thinned to fit). Without it, outputs get their channels as written in the file.")]
    public bool Arrange { get; init; }

    [CommandOption("--tap <PORT>")]
    [Description("MIDI input that taps the tempo, e.g. a foot pedal or pad (index, name or part of a name from [bold]metrodom devices[/]). Works alongside the Space key.")]
    public string? Tap { get; init; }

    [CommandOption("--tap-on <TRIGGER>")]
    [Description("Which messages from [bold]--tap[/] count: [bold]any[/] (default: Note On or a pedal press), [bold]note[/][[:N]], [bold]cc[/][[:N]] (press = value ≥ 64), [bold]pc[/][[:N]], optionally @CHANNEL (e.g. [bold]cc:64[/], [bold]note:36@10[/]).")]
    public string? TapOn { get; init; }

    [CommandOption("--clock-in <PORT>")]
    [Description("Follow the tempo of MIDI Clock from this input (a drum machine, a DAW). When the clock stops, the band holds its tempo.")]
    public string? ClockIn { get; init; }

    public TapTrigger Trigger => TapTrigger.TryParse(TapOn, out var trigger, out _) ? trigger : new TapTrigger();

    [CommandOption("--follow <PORT>")]
    [Description("Follow a human player's beat from this input (drum triggers, a pad, an e-kit): the band locks onto their tempo [bold]and[/] phase, predicts their next beats, holds tempo through a stop, and starts each song when they count in (4 steady hits). See [bold]--follow-on[/].")]
    public string? Follow { get; init; }

    [CommandOption("--follow-on <TRIGGER>")]
    [Description("Which messages from [bold]--follow[/] are hits, as [bold]--tap-on[/] (default [bold]any[/]). E.g. [bold]note:38[/] to follow the snare only, [bold]note@10[/] for any drum.")]
    public string? FollowOn { get; init; }

    public TapTrigger FollowTrigger => TapTrigger.TryParse(FollowOn, out var trigger, out _) ? trigger : new TapTrigger();

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

    public (int Channel, int Note) ClickNote => PlayCommand.TryParseClick(Click, out var click, out _) ? click : (9, 76);

    [CommandOption("--api <ADDRESS>")]
    [Description("Serve the Control API (HTTP + JSON at /api/v0) for apps: [bold]PORT[/] on this computer only (e.g. [bold]7890[/]) or [bold]HOST:PORT[/] (e.g. [bold]0.0.0.0:7890[/] for the LAN). The token shown at start (or METRODOM_API_TOKEN) is the owner's; the owner hands out expiring operator and guest tokens, and the policy's [bold]roles[/] say what each may do.")]
    public string? Api { get; init; }

    public ApiEndpoint? ApiAt => Api is not null && MetroDom.Conductor.Api.ApiEndpoint.TryParse(Api, out var endpoint, out _) ? endpoint : null;

    [CommandOption("--lights <RIG>")]
    [Description("Light profile (metrodom.light-profile/0) to drive over sACN: reactive lights, each instrument's fixtures pulse in its color with its notes, timed to land with the sound. Panic and the end of the set show the rig's safe look. Check a rig first with [bold]metrodom lights[/].")]
    public string? Lights { get; init; }

    [CommandOption("--lights-pad <PORT>")]
    [Description("MIDI input for live light cues: its notes recall the rig's scenes and its master CC dims, on any channel, overriding reactive lights (a 4×4 pad grid sends notes 36-51). Needs [bold]--lights[/].")]
    public string? LightsPad { get; init; }

    public LightProfile? LightProfile =>
        Lights is not null && File.Exists(Lights) && LightProfileJson.TryParse(File.ReadAllText(Lights), out var rig, out _) ? rig : null;

    [CommandOption("-t|--tempo <BPM>")]
    [Description("Start at this tempo instead of the file's. Tempo changes in the file scale with it.")]
    public double? Tempo { get; init; }

    public override ValidationResult Validate()
    {
        if (Api is not null && !MetroDom.Conductor.Api.ApiEndpoint.TryParse(Api, out _, out var apiError))
        {
            return ValidationResult.Error($"--api: {apiError}");
        }

        if (Lights is not null)
        {
            if (!File.Exists(Lights))
            {
                return ValidationResult.Error($"--lights: file not found: {Lights}");
            }

            if (!LightProfileJson.TryParse(File.ReadAllText(Lights), out _, out var lightsError))
            {
                return ValidationResult.Error($"--lights: {lightsError}");
            }
        }

        if (LightsPad is not null && Lights is null)
        {
            return ValidationResult.Error("--lights-pad needs --lights <RIG>.");
        }

        if (LightsPad is not null && (LightsPad == Control || LightsPad == Tap || LightsPad == ClockIn || LightsPad == Follow))
        {
            return ValidationResult.Error("--lights-pad needs a port of its own.");
        }

        if (Control is not null && (Control == Tap || Control == ClockIn || Control == Follow))
        {
            return ValidationResult.Error("--control and --tap/--clock-in/--follow can't share a port: map tap in the policy instead.");
        }

        if (Follow is not null && (Tap is not null || ClockIn is not null))
        {
            return ValidationResult.Error("--follow sets the tempo from the player; drop --tap/--clock-in.");
        }

        if (!TapTrigger.TryParse(FollowOn, out _, out var followError))
        {
            return ValidationResult.Error($"--follow-on: {followError}");
        }

        if (FollowOn is not null && Follow is null)
        {
            return ValidationResult.Error("--follow-on needs --follow <PORT>.");
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

        if (!PlayCommand.TryParseClick(Click, out _, out var clickError))
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

/// <summary>How a set runs: its queue, how a queued song becomes a file, and whether it outlives its songs.</summary>
/// <param name="Queue">The set.</param>
/// <param name="Resolve">The file to play for a queued song, or null when it's gone (skipped with a message).</param>
/// <param name="KeepRunning">Wait for more songs when the queue runs dry (<c>serve</c>), instead of ending.</param>
/// <param name="StartApi">Starts the Control API (and whatever goes with it) once the band is up, or returns null.</param>
internal sealed record SetOptions(SetQueue Queue, Func<QueuedSong, string?> Resolve, bool KeepRunning, Func<LiveControl, Session, IDisposable?> StartApi);
