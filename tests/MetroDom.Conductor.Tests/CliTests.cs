using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using Spectre.Console;

namespace MetroDom.Conductor.Tests;

public class CliTests
{
    [Theory]
    [InlineData("10", new[] { 9 })]
    [InlineData(" 11, 10 ,10", new[] { 9, 10 })]
    [InlineData("1,16", new[] { 0, 15 })]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    public void ChannelList_ParsesMusicianNumbering(string? raw, int[] expected)
    {
        Assert.True(ChannelList.TryParse(raw, out var channels, out _));
        Assert.Equal(expected, channels);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("drums")]
    public void ChannelList_RejectsNonChannels(string raw)
    {
        Assert.False(ChannelList.TryParse(raw, out _, out var error));
        Assert.Contains(raw, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("inspect")]
    [InlineData("inspect", "missing.mid")]
    [InlineData("play", "missing.mid", "--output", "0")]
    [InlineData("inspect", "song.mid", "--no-such-option")]
    public void UsageErrors_ExitWith2(params string[] args) =>
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));

    [Fact]
    public void Play_InvalidMute_ExitsWith2BeforeOpeningAnyDevice()
    {
        var path = WriteSong();
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "--mute", "17"], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inspect_ValidSong_ExitsWith0()
    {
        var path = WriteSong();
        try
        {
            Assert.Equal(0, MetroDomApp.Create().Run(["inspect", path], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Report_MapsErrorsToDocumentedExitCodes()
    {
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Report(new UsageException("x")));
        Assert.Equal(MetroDomApp.PlatformError, MetroDomApp.Report(new PlatformNotSupportedException("x")));
        Assert.Equal(MetroDomApp.DeviceError, MetroDomApp.Report(new MidiDeviceException("x")));
    }

    [Fact]
    public void StatusPanel_ShowsOutputMutesActivityAndWarnings()
    {
        using var session = new Session(new FakePort("Synth [1]"), mutedChannels: [9]);
        session.Activity.Record(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100));
        session.Messages.Add("MIDI Synth: unplugged");

        var text = Render(LiveStatus.Panel("▶ song.mid", session, LiveStatus.Progress(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2))));

        Assert.Contains("Synth [1]", text, StringComparison.Ordinal); // device names are escaped, not parsed as markup
        Assert.Contains("connected", text, StringComparison.Ordinal);
        Assert.Contains("0:30 / 2:00", text, StringComparison.Ordinal);
        Assert.Contains("M", text, StringComparison.Ordinal);
        Assert.Contains("unplugged", text, StringComparison.Ordinal);
    }

    private static string Render(Spectre.Console.Rendering.IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 100;
        console.Write(renderable);
        return writer.ToString();
    }

    private static string WriteSong()
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-cli-{Guid.NewGuid():N}.mid");
        new MidiFile(new TrackChunk(
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100),
            new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 480 })).Write(path);
        return path;
    }

    private sealed class FakePort(string name) : IMidiOutputPort
    {
        public string Name { get; } = name;

        public void Send(MidiEvent midiEvent)
        {
        }

        public void Dispose()
        {
        }
    }
}
