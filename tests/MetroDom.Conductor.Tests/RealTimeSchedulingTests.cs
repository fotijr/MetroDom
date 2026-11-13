using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

/// <summary>
/// Tests that measure wall-clock lateness. They run on their own, after the parallel tests, so a
/// busy test run can't steal the scheduler thread's CPU and make the timing look worse than it is.
/// </summary>
[CollectionDefinition(nameof(RealTime), DisableParallelization = true)]
public sealed class RealTime;

[Collection(nameof(RealTime))]
public sealed class RealTimeSchedulingTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void SchedulerLoop_SendsOnTimeInRealTime_AndFollowsASpeedChange()
    {
        var clock = new SystemClock();
        using var session = new Session([new SessionOutput(new PlaySchedulingTests.RecordingPort("fast"), [0], Ms(0)), new SessionOutput(new PlaySchedulingTests.RecordingPort("slow"), [1], Ms(30))], []);
        IMidiOutput fast = session.Governors[0], slow = session.Governors[1];
        var scheduler = new LatencyScheduler(session.Orchestra, clock, session.Profiles);
        var sends = new ConcurrentQueue<(ScheduledSend Send, TimeSpan At)>();
        using var firstBeat = new ManualResetEventSlim();
        scheduler.Sent += send =>
        {
            sends.Enqueue((send, clock.Now));
            if (send.Output == fast && send.SongTime == TimeSpan.Zero)
            {
                firstBeat.Set();
            }
        };
        TimedMidiEvent Hit(double ms, int channel) => new(Ms(ms), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)channel });

        // Same start-up as `play`: a panic warms every send path, then a 250 ms pre-roll.
        session.Orchestra.Panic();
        scheduler.Start([Hit(0, 0), Hit(0, 1), Hit(200, 0), Hit(200, 1), Hit(400, 0), Hit(400, 1)], preroll: Ms(250));
        using var finished = new ManualResetEventSlim();
        using var loop = new SchedulerLoop(scheduler, clock);
        loop.Finished += finished.Set;
        loop.Start();

        // Double the tempo just after beat 1, while the loop is waiting for beat 2.
        Assert.True(firstBeat.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "beat 1 never sent");
        scheduler.SetSpeed(2);
        loop.Wake();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "loop never finished");

        var all = sends.ToArray();
        var f = all.Where(x => x.Send.Output == fast).ToArray();
        var s = all.Where(x => x.Send.Output == slow).ToArray();
        Assert.Equal(3, f.Length);
        Assert.Equal(3, s.Length);

        // Logic, exact: the slow instrument is due 30 ms before the fast one on every beat.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(30, (f[i].Send.DueAt - s[i].Send.DueAt).TotalMilliseconds, 0.01);
        }

        // Beat 1 → 2 is 200 song-ms, mostly at double speed: under 200 ms. Beat 2 → 3 is all at double speed: 100 ms.
        Assert.InRange((f[1].Send.DueAt - f[0].Send.DueAt).TotalMilliseconds, 100, 199);
        Assert.Equal(100, (f[2].Send.DueAt - f[1].Send.DueAt).TotalMilliseconds, 0.01);

        // Real time: never early; late only by how long the OS took to run the thread.
        Assert.All(all, x => Assert.True(x.At >= x.Send.DueAt, $"sent {(x.Send.DueAt - x.At).TotalMilliseconds:0.###} ms early"));
        var lateness = all.Select(x => (x.At - x.Send.DueAt).TotalMilliseconds).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"Lateness (ms): {string.Join(", ", lateness.Select(l => l.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}");
        Assert.InRange(lateness.Max(), 0, 1000);
        if (Environment.GetEnvironmentVariable("CI") is null)
        {
            Assert.InRange(lateness.Max(), 0, 5);
        }
    }
}
