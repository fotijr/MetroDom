using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MetroDom.Conductor.Midi;

namespace MetroDom.Conductor.Commands;

internal static class PlayCommand
{
    public static int Run(CommandLine cli)
    {
        var path = cli.Positional(0) ?? throw new UsageException("play: missing <song.mid>.");
        if (!File.Exists(path))
        {
            throw new UsageException($"play: file not found: {path}");
        }

        var midiFile = MidiFile.Read(path);
        using var session = new Session(cli);
        using var device = new OrchestraPlaybackDevice(session.Orchestra);
        using var playback = midiFile.GetPlayback(device);
        playback.Finished += (_, _) => session.RequestStop();

        Console.WriteLine($"Playing {Path.GetFileName(path)}. Ctrl+C to stop.");
        playback.Start();
        session.WaitForStop();
        playback.Stop();
        return 0;
    }
}
