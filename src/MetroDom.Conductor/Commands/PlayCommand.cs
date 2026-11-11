using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
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


        [CommandOption("-t|--tempo <BPM>")]
        [Description("Start at this tempo instead of the file's. Tempo changes in the file scale with it.")]
        public double? Tempo { get; init; }

        public override ValidationResult Validate()
        {
            if (!File.Exists(Path))
            {
                return ValidationResult.Error($"file not found: {Path}");
            }

            return Tempo is null or (>= TapTempo.MinBpm and <= TapTempo.MaxBpm)
                ? base.Validate()
                : ValidationResult.Error($"--tempo must be {TapTempo.MinBpm:0}-{TapTempo.MaxBpm:0} BPM.");
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var midiFile = MidiFile.Read(settings.Path);
        var tempoMap = midiFile.GetTempoMap();
        var song = System.IO.Path.GetFileName(settings.Path);
        var fileBpm = FileBpm(tempoMap, TimeSpan.Zero);
        var startSpeed = settings.Tempo is { } bpm ? Clamp(bpm / fileBpm) : 1;

        string output;
        using (var session = new Session(settings))
        {
            output = session.OutputName;
            var clock = new SystemClock();
            var scheduler = new LatencyScheduler(session.Orchestra, clock, session.Profiles);
            scheduler.Sent += s => session.Activity.Record(s.Event);
            var timeline = SongTimeline.From(midiFile);

            // Start from silence. Also warms up every output's send path, so the first note isn't late.
            session.Orchestra.Panic();
            scheduler.Start(timeline, startSpeed, Preroll);

            using var loop = new SchedulerLoop(scheduler, clock);
            loop.Finished += session.RequestStop;
            loop.Failed += ex =>
            {
                session.Messages.Add($"Scheduler stopped: {ex.Message}");
                session.RequestStop();
            };

            var tap = new TapTempo();
            void SetTempo(double bpm)
            {
                scheduler.SetSpeed(Clamp(bpm / FileBpm(tempoMap, scheduler.Position)));
                loop.Wake();
            }

            if (!Ui.IsInteractive)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Playing {song} to {output} at {fileBpm * startSpeed:0.#} BPM, lookahead {scheduler.Lookahead.TotalMilliseconds:0.#} ms{Muted(settings)}. Ctrl+C to stop."));
            }

            loop.Start();
            LiveStatus.Run(
                session,
                () => LiveStatus.Panel(
                    $"▶ {Markup.Escape(song)}",
                    session,
                    LiveStatus.Progress(scheduler.Position, scheduler.Duration),
                    TempoLine(scheduler, FileBpm(tempoMap, scheduler.Position), tap.Bpm is not null),
                    "Space tap · +/- tempo · 0 file tempo"),
                key =>
                {
                    switch (key.KeyChar)
                    {
                        case ' ' or 't' or 'T':
                            if (tap.Tap(clock.Now) is { } tapped)
                            {
                                SetTempo(tapped);
                            }

                            break;
                        case '+' or '=':
                            scheduler.SetSpeed(Clamp(scheduler.Speed * Nudge));
                            loop.Wake();
                            break;
                        case '-' or '_':
                            scheduler.SetSpeed(Clamp(scheduler.Speed / Nudge));
                            loop.Wake();
                            break;
                        case '0':
                            scheduler.SetSpeed(1);
                            loop.Wake();
                            break;
                    }
                });
        }

        Ui.Success($"Stopped. Panic sent to {output}.");
        return 0;
    }

    /// <summary>The file's tempo at a song position (tempo maps can change mid-song).</summary>
    private static double FileBpm(TempoMap tempoMap, TimeSpan position) =>
        tempoMap.GetTempoAtTime(new MetricTimeSpan(position)).BeatsPerMinute;

    private static double Clamp(double speed) => Math.Clamp(speed, LatencyScheduler.MinSpeed, LatencyScheduler.MaxSpeed);

    private static Markup TempoLine(LatencyScheduler scheduler, double fileBpm, bool tapped) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"[grey]Tempo[/]   [bold]{fileBpm * scheduler.Speed:0.#} BPM[/] [grey](file {fileBpm:0.#} × {scheduler.Speed:0.00}{(tapped ? ", tapped" : "")}) · lookahead {scheduler.Lookahead.TotalMilliseconds:0.#} ms[/]"));

    private static string Muted(OutputSettings settings) =>
        settings.MutedChannels.Count > 0 ? $" (muted: {ChannelList.Format(settings.MutedChannels)})" : "";
}
