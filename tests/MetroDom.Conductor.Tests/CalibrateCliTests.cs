using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Api;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Calibration;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

/// <summary>Calibration over real (fake) ports, the boot check and the CLI (plan 0004 S5).</summary>
public class CalibrateCliTests
{
    private static readonly CalibrationOptions Fast = new() { Repeats = 2, Window = TimeSpan.FromMilliseconds(40), Settle = TimeSpan.Zero, LeadIn = TimeSpan.Zero };

    /// <summary>An instrument whose onset the "mic" hears the moment the note is sent.</summary>
    private sealed class HeardAtOnce(string name, OnsetInput mic) : IMidiOutputPort
    {
        public List<MidiEvent> Sent { get; } = [];

        public string Name => name;

        public void Send(MidiEvent midiEvent)
        {
            Sent.Add(midiEvent);
            if (midiEvent is NoteOnEvent on)
            {
                mic.Raise(new NoteOnEvent(on.NoteNumber, on.Velocity) { Channel = on.Channel });
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class OnsetInput : IMidiInputPort
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

    private static (Session Session, HeardAtOnce Port, OnsetInput Mic) Band(double declaredMs, int channel = 0)
    {
        var mic = new OnsetInput();
        var port = new HeardAtOnce("bell", mic);
        var profile = new InstrumentProfile("bell", TimeSpan.FromMilliseconds(declaredMs)) { Notes = new HashSet<int> { 60, 62, 64, 65 } };
        return (new Session([new SessionOutput(port, [channel], profile)], []), port, mic);
    }

    [Fact]
    public void Measure_strikes_each_note_on_the_outputs_channel_and_times_its_onset()
    {
        var (session, port, mic) = Band(0, channel: 3);
        using (session)
        {
            var clock = new SystemClock();
            using var listener = new OnsetListener(mic, new TapTrigger(), clock);

            var result = Calibrator.Measure(session.Governors[0], 3, [60, 64], Fast, listener, clock, session.WaitForStop);

            Assert.True(result.HeardAll);
            Assert.All(result.Notes, n => Assert.InRange(n.Median!.Value.TotalMilliseconds, 0, 5));
            var ons = port.Sent.OfType<NoteOnEvent>().ToArray();
            Assert.Equal([60, 64, 60, 64], ons.Select(e => (int)e.NoteNumber));
            Assert.All(port.Sent.OfType<ChannelEvent>(), e => Assert.Equal(3, (int)e.Channel));
            Assert.Equal(4, port.Sent.OfType<NoteOffEvent>().Count()); // nothing left sounding
        }
    }

    [Fact]
    public void Only_onsets_matching_the_trigger_count()
    {
        var (session, _, mic) = Band(0);
        using (session)
        {
            var clock = new SystemClock();
            Assert.True(TapTrigger.TryParse("note:99", out var trigger, out _));
            using var listener = new OnsetListener(mic, trigger, clock);

            Assert.True(Calibrator.Measure(session.Governors[0], 0, [60], Fast, listener, clock, session.WaitForStop).HeardNone);
        }
    }

    [Fact]
    public void Boot_check_flags_drift_in_messages_and_the_api()
    {
        var (session, _, mic) = Band(30); // declares 30 ms, sounds at once
        using (session)
        {
            var clock = new SystemClock();
            using var listener = new OnsetListener(mic, new TapTrigger(), clock);

            var checks = LatencyBootCheck.Run(session, listener, clock, Fast);

            Assert.Equal(CheckStatus.Drifted, checks["bell"].Status);
            Assert.Contains(session.Messages.Snapshot(), m => m.Contains("drifted", StringComparison.Ordinal) && m.Contains("metrodom calibrate", StringComparison.Ordinal));
            var json = JsonSerializer.SerializeToElement(ControlApi.Instruments(session)[0]);
            var check = json.GetProperty("latencyCheck");
            Assert.Equal("drifted", check.GetProperty("status").GetString());
            Assert.InRange(check.GetProperty("worstDriftMs").GetDouble(), -31, -25);
        }
    }

    [Fact]
    public void Instruments_show_no_check_until_one_ran()
    {
        var (session, _, _) = Band(0);
        using (session)
        {
            var json = JsonSerializer.SerializeToElement(ControlApi.Instruments(session)[0]);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("latencyCheck").ValueKind);
        }
    }

    [Fact]
    public void Measured_latencies_beat_declared_ones_but_not_the_command_line()
    {
        var book = new CalibrationBook();
        book.Set(new MeasuredLatency("bell", DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(14), new Dictionary<int, TimeSpan> { [60] = TimeSpan.FromMilliseconds(16) }));
        var sent = new InstrumentProfile("bell", TimeSpan.FromMilliseconds(10));
        var notes = new List<string>();

        Assert.True(OutputSpec.TryParse("bell", out var plain, out _));
        Assert.True(OutputSpec.TryParse("bell@25", out var told, out _));
        var applied = Session.Calibrated(book, "bell", plain, sent, notes.Add);

        Assert.Equal((16, 14), (applied.LatencyFor(60).TotalMilliseconds, applied.LatencyFor(62).TotalMilliseconds));
        Assert.Single(notes);
        Assert.Same(sent, Session.Calibrated(book, "bell", told, sent, notes.Add));
        Assert.Same(sent, Session.Calibrated(book, "other", plain, sent, notes.Add));
    }

    [Fact]
    public void A_broken_calibrations_file_is_reported_and_ignored()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");
            var warnings = new List<string>();
            Assert.Empty(CalibrationStore.Load(path, warnings.Add).All);
            Assert.Contains(path, Assert.Single(warnings), StringComparison.Ordinal);

            var book = new CalibrationBook();
            book.Set(new MeasuredLatency("bell", DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(14), new Dictionary<int, TimeSpan>()));
            CalibrationStore.Save(book, path);
            Assert.NotNull(CalibrationStore.Load(path).Find("bell"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("calibrate", "-o", "x")]
    [InlineData("calibrate", "-o", "x", "--onsets", "mic", "--repeats", "0")]
    [InlineData("calibrate", "-o", "x", "--onsets", "mic", "--window", "10")]
    [InlineData("calibrate", "-o", "x", "--onsets", "mic", "--onsets-on", "pedal")]
    [InlineData("play", "--onsets-on", "note:38")]
    [InlineData("play", "--onset-latency", "3")]
    [InlineData("virtual", "--jitter", "-1")]
    public void Bad_options_are_usage_errors(params string[] args) =>
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));
}
