using System.ComponentModel;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

internal sealed class PlayCommand : Command<PlayCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandArgument(0, "<SONG>")]
        [Description("Standard MIDI File to play (.mid).")]
        public string Path { get; init; } = "";

        public override ValidationResult Validate() =>
            File.Exists(Path) ? base.Validate() : ValidationResult.Error($"file not found: {Path}");
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var midiFile = MidiFile.Read(settings.Path);
        var song = System.IO.Path.GetFileName(settings.Path);
        string output;
        using (var session = new Session(settings))
        {
            output = session.OutputName;
            using var device = new OrchestraPlaybackDevice(session.Orchestra, session.Activity.Record);
            using var playback = midiFile.GetPlayback(device);
            playback.Finished += (_, _) => session.RequestStop();
            TimeSpan duration = playback.GetDuration<MetricTimeSpan>();

            if (!Ui.IsInteractive)
            {
                Console.WriteLine($"Playing {song} to {output}" + Muted(settings) + ". Ctrl+C to stop.");
            }

            playback.Start();
            LiveStatus.Run(session, () => LiveStatus.Panel(
                $"▶ {Markup.Escape(song)}",
                session,
                LiveStatus.Progress(playback.GetCurrentTime<MetricTimeSpan>(), duration)));
            playback.Stop();
        }

        Ui.Success($"Stopped. Panic sent to {output}.");
        return 0;
    }

    private static string Muted(OutputSettings settings) =>
        settings.MutedChannels.Count > 0 ? $" (muted: {ChannelList.Format(settings.MutedChannels)})" : "";
}
