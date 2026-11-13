using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Bodies;
using MetroDom.Core.Calibration;
using MetroDom.Core.Safety;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

/// <summary><c>metrodom bench</c> against the virtual brain, on a virtual clock (plan 0006 N6).</summary>
public class BenchTests
{
    private static BodyDefinition M1()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        Assert.True(BodyImage.TryParseJson(File.ReadAllText(Path.Combine(dir!.FullName, "Instruments", "xylophone_solenoid", "body.json")), out var body, out _));
        return body;
    }

    /// <summary>Answers from a script, in order; null once it runs out (like a pipe).</summary>
    private sealed class Script(params string?[] answers) : IBenchOperator
    {
        private readonly Queue<string?> _answers = new(answers);

        public List<string> Questions { get; } = [];

        public string? Ask(string question, IReadOnlyList<string> choices)
        {
            Questions.Add(question);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        public void Say(string markup)
        {
        }
    }

    private sealed class Mic : IMidiInputPort
    {
        public string Name => "mic";

        public event Action<MidiEvent>? EventReceived;

        public void Raise(MidiEvent e) => EventReceived?.Invoke(e);

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private static readonly BenchOptions Short = new()
    {
        Soak = TimeSpan.FromSeconds(10),
        Calibration = new CalibrationOptions { Repeats = 2, Window = TimeSpan.FromMilliseconds(100), Settle = TimeSpan.Zero, LeadIn = TimeSpan.Zero },
    };

    private static (VirtualClock Clock, VirtualInstrument Brain, SafetyGovernor Governor) Rig()
    {
        var clock = new VirtualClock();
        var brain = VirtualInstrument.ForBody(M1(), clock);
        return (clock, brain, new SafetyGovernor(brain, brain.Profile, SafetyLimits.None, clock));
    }

    /// <summary>A wait that moves the virtual clock (a hair past, so spin-waits end).</summary>
    private static Func<TimeSpan, bool> Advance(VirtualClock clock) => t =>
    {
        clock.AdvanceTo(clock.Now + t + TimeSpan.FromMilliseconds(1));
        return false;
    };

    [Fact]
    public void EveryNote_SoftAndHard_ThenASoakTheBrainNeverRefuses()
    {
        var (clock, brain, governor) = Rig();
        var op = new Script("both", "again", "soft only", "both", "warm");

        var report = Bench.Run(governor, 0, op, clock, Advance(clock), Short);

        Assert.Equal([72, 74, 76], report.Notes.Select(n => n.Note));
        Assert.Equal(["both", "soft only", "both"], report.Notes.Select(n => n.Heard));
        Assert.Equal(4, op.Questions.Count(q => q.StartsWith("Heard", StringComparison.Ordinal))); // "again" asks once more
        Assert.Equal("warm", report.Coil);
        Assert.Equal(72, report.SoakNote);
        Assert.True(report.SoakStruck > 0);
        Assert.True(report.SoakHeld > 0); // 30 ms every 60 ms is 50%: the 30% duty thins it
        Assert.Equal(0, brain.Refused);   // the governor held back everything the brain would refuse
        Assert.Contains(brain.Strikes, s => s.Note == 74 && s.PulseMs < 30); // the soft strike is shorter
        Assert.Null(report.Calibration);
    }

    [Fact]
    public void Piped_NobodyAnswers_AndTheReportSaysSo()
    {
        var (clock, _, governor) = Rig();

        var report = Bench.Run(governor, 0, new Script(), clock, Advance(clock), Short with { Soak = TimeSpan.Zero });
        var markdown = report.Markdown();

        Assert.All(report.Notes, n => Assert.Null(n.Heard));
        Assert.Contains("| C5 (72) | not asked | not measured |", markdown, StringComparison.Ordinal);
        Assert.Contains("Soak: skipped.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAnOnsetInput_EachNotesLatencyIsMeasured()
    {
        var (clock, brain, governor) = Rig();
        var mic = new Mic();
        brain.Struck += (_, s) => mic.Raise(new NoteOnEvent(new(72), new(100)));
        using var onsets = new OnsetListener(mic, new TapTrigger(), clock);

        var report = Bench.Run(governor, 0, new Script(), clock, Advance(clock), Short with { Soak = TimeSpan.Zero }, onsets);

        Assert.NotNull(report.Calibration);
        Assert.True(report.Calibration.HeardAll);
        Assert.All(report.Notes, n => Assert.NotNull(n.Latency));
        Assert.Contains("| E5 (76) | not asked | 0 ms |", report.Markdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void Stopping_EndsTheRun_WithWhatItHas()
    {
        var (clock, _, governor) = Rig();

        var report = Bench.Run(governor, 0, new Script(), clock, _ => true, Short);

        Assert.Empty(report.Notes);
        Assert.Equal(0, report.SoakStruck);
    }

    [Theory]
    [InlineData("--soak", "700")]
    [InlineData("--soak-note", "C4-C5")]
    public void Command_RefusesBadOptions(string option, string value) =>
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["bench", "anything", option, value], TestContext.Current.CancellationToken));
}
