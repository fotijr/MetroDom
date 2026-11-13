using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Safety;
using Spectre.Console;

namespace MetroDom.Conductor.Tests;

public class SafetyCliTests
{
    [Theory]
    [InlineData("23:00-07:00", 23, 0, 7, 0, 0)]
    [InlineData("9:30-10:00@40", 9, 30, 10, 0, 40)]
    [InlineData(" 22:00 - 08:00 @ 0 ", 22, 0, 8, 0, 0)]
    public void QuietHours_Parse(string raw, int sh, int sm, int eh, int em, int velocity)
    {
        Assert.True(QuietHoursSpec.TryParse(raw, out var quiet, out var error), error);
        Assert.Equal(new QuietHours(new TimeOnly(sh, sm), new TimeOnly(eh, em), velocity), quiet);
    }

    [Theory]
    [InlineData("23-07")]
    [InlineData("25:00-07:00")]
    [InlineData("23:00-07:00@200")]
    [InlineData("08:00-08:00")]
    [InlineData("late")]
    public void QuietHours_RejectBadSpecs(string raw) =>
        Assert.False(QuietHoursSpec.TryParse(raw, out _, out _));

    [Theory]
    [InlineData("23-07")]
    [InlineData("23:00-07:00@40")]
    public void BadCurfew_ExitsWith2BeforeOpeningAnyDevice(string value)
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-curfew-{Guid.NewGuid():N}.mid");
        Core.Simulation.DemoSong.Create().Write(path);
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "-o", "0", "--curfew", value], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("--max-velocity", "0")]
    [InlineData("--max-velocity", "128")]
    [InlineData("--max-rate", "0")]
    [InlineData("--quiet", "23-07")]
    public void BadSafetyOptions_ExitWith2BeforeOpeningAnyDevice(string option, string value)
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-safety-{Guid.NewGuid():N}.mid");
        Core.Simulation.DemoSong.Create().Write(path);
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "-o", "0", option, value], TestContext.Current.CancellationToken));
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["route", "-i", "0", "-o", "0", option, value], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, relative)))
            {
                return Path.Combine(dir.FullName, relative);
            }
        }

        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void PolicyLimits_ReachTheGovernors_WithFlagsOnTop()
    {
        var rental = RepoFile("policies/rental.json");
        var settings = new PlayCommand.Settings { Policy = rental, MaxVelocity = 120, MaxRate = 8 };
        Assert.Equal("rental", settings.PolicyProfile.Mode);
        Assert.Equal(100, settings.Limits.MaxVelocity);       // the policy's 100 beats the looser flag
        Assert.Equal(8, settings.Limits.MaxNotesPerSecond);   // the tighter flag beats the policy's 16
        Assert.Equal(new QuietHours(new TimeOnly(22, 0), new TimeOnly(8, 0)), settings.Limits.Quiet);

        var tonight = new PlayCommand.Settings { Policy = rental, Quiet = "23:30-07:00@20" };
        Assert.Equal(new QuietHours(new TimeOnly(23, 30), new TimeOnly(7, 0), 20), tonight.Limits.Quiet);
    }

    [Fact]
    public void BadPolicyFile_IsAUsageErrorForPlayAndRoute()
    {
        var policy = Path.Combine(Path.GetTempPath(), $"metrodom-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(policy, """{"schema":"metrodom.policy/0","mode":"x","roles":{"guest":["panic"]}}""");
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["route", "-i", "0", "-o", "0", "--policy", policy], TestContext.Current.CancellationToken));
            var error = new RouteCommand.Settings { Policy = policy }.Validate().Message;
            Assert.Contains("guests can't have", error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(policy);
        }
    }

    [Fact]
    public void Session_SendsEverythingThroughGovernors_AndReportsWhatWasHeldBack()
    {
        var port = new CountingPort("xylo");
        string[] summary;
        string panel;
        using (var session = new Session(
            [new SessionOutput(port, [0], TimeSpan.Zero, new HashSet<int> { 60 })],
            mutedChannels: [],
            new SafetyLimits { MaxVelocity = 64 }))
        {
            session.Orchestra.Route(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)127));
            session.Orchestra.Route(new NoteOnEvent((SevenBitNumber)61, (SevenBitNumber)100));
            summary = [.. session.SafetySummary()];
            panel = Render(LiveStatus.Panel("⇄ keys", session, progress: null));
        }

        Assert.Equal([64], port.NoteVelocities);
        Assert.Equal("xylo: 1 not playable, 1 turned down", Assert.Single(summary));
        Assert.Contains("1 held back", panel, StringComparison.Ordinal);
        Assert.True(port.PanicCount > 0, "dispose still panics through the governor");
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

    private sealed class CountingPort(string name) : IMidiOutputPort
    {
        public string Name { get; } = name;

        public List<int> NoteVelocities { get; } = [];

        public int PanicCount { get; private set; }

        public void Send(MidiEvent midiEvent)
        {
            switch (midiEvent)
            {
                case NoteOnEvent on:
                    NoteVelocities.Add(on.Velocity);
                    break;
                case ControlChangeEvent:
                    PanicCount++;
                    break;
            }
        }

        public void Dispose()
        {
        }
    }
}
