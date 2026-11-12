using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Lighting;
using MetroDom.Core.Lighting;

namespace MetroDom.Conductor.Tests;

public class LightsTests
{
    private const string Rig = """
        {"schema":"metrodom.light-profile/0","name":"Test rig",
         "fixtures":[{"id":"par","address":1,"channels":["dimmer","red","green","blue"]},
                     {"id":"strip","universe":2,"address":1,"channels":["red","green","blue"],"count":3}],
         "scenes":[{"note":36,"name":"Red","set":{"*":{"dimmer":255,"red":255}}},
                   {"note":37,"name":"Blue","set":{"*":{"dimmer":255,"blue":255}}}]}
        """;

    private static readonly LightProfile Profile = LightProfileJson.TryParse(Rig, out var p, out _) ? p : throw new InvalidOperationException();

    [Fact]
    public void Output_SendsAChangeThreeTimes_ThenKeepsAliveEverySecond()
    {
        var socket = new FakeSocket();
        var time = new ManualTime();
        using var output = new SacnOutput(socket, new LightOutput(), "MetroDom test", time: time, start: false);

        output.Show(LightRenderer.Render(Profile, Profile.SceneFor(36)!.Look));
        for (var i = 0; i < 5; i++)
        {
            output.Pump();
            time.Advance(SacnOutput.Tick);
        }

        Assert.Equal(3, socket.For(1).Count); // change + 2 repeats, not every tick
        Assert.Equal(3, socket.For(2).Count);
        Assert.Equal([0, 1, 2], socket.For(1).Select(p => (int)p.Sequence));
        Assert.All(socket.Sent, s => Assert.Equal(E131.Port, s.Port));
        Assert.Equal("239.255.0.1", socket.Sent.First(s => s.Packet.Universe == 1).Host);
        Assert.Equal("239.255.0.2", socket.Sent.First(s => s.Packet.Universe == 2).Host);
        Assert.Equal(255, socket.For(1)[0].Data[1]); // red

        time.Advance(SacnOutput.KeepAlive);
        output.Pump();
        Assert.Equal(4, socket.For(1).Count);

        // The same look again isn't a change; a new one is.
        output.Show(LightRenderer.Render(Profile, Profile.SceneFor(36)!.Look));
        output.Pump();
        Assert.Equal(4, socket.For(1).Count);
        output.Show(LightRenderer.Render(Profile, Profile.SceneFor(37)!.Look));
        output.Pump();
        Assert.Equal(5, socket.For(1).Count);
        Assert.Equal(255, socket.For(1)[^1].Data[3]); // blue
        Assert.False(socket.For(1)[^1].Terminated);
    }

    [Fact]
    public void Output_Dispose_TerminatesEveryStreamThreeTimes()
    {
        var socket = new FakeSocket();
        var output = new SacnOutput(socket, new LightOutput(Destination: "192.168.1.40", Priority: 150), "MetroDom test", start: false);
        output.Show(LightRenderer.Render(Profile, Profile.SafeLook));

        output.Dispose();
        output.Dispose();
        output.Pump();

        Assert.Equal(6, socket.Sent.Count);
        Assert.All(socket.Sent, s => Assert.True(s.Packet.Terminated));
        Assert.All(socket.Sent, s => Assert.Equal("192.168.1.40", s.Host)); // unicast when the profile says so
        Assert.All(socket.Sent, s => Assert.Equal(150, s.Packet.Priority));
        Assert.True(socket.Disposed);
    }

    [Fact]
    public void Output_FailedSends_AreLoggedOnce_AndNeverThrown()
    {
        var socket = new FakeSocket { Fail = true };
        var time = new ManualTime();
        var log = new List<string>();
        using var output = new SacnOutput(socket, new LightOutput(), "MetroDom test", time: time, log: log.Add, start: false);
        output.Show(LightRenderer.Render(Profile, Profile.SceneFor(36)!.Look));

        output.Pump();
        output.Pump();
        Assert.Contains("can't send", Assert.Single(log), StringComparison.Ordinal);

        socket.Fail = false;
        output.Pump();
        Assert.Equal("sACN: sending again.", log[^1]);
    }

    [Fact]
    public void Lights_ChecksTheRig_AndPicksScenesByNoteOrName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-rig-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, Rig);
        try
        {
            var run = (string[] args) => MetroDomApp.Create().Run(["lights", path, .. args], TestContext.Current.CancellationToken);
            Assert.Equal(0, run([]));
            Assert.Equal(MetroDomApp.UsageError, run(["--scene", "99"]));
            Assert.Equal(MetroDomApp.UsageError, run(["--scene", "36", "--safe"]));
            Assert.Equal(MetroDomApp.UsageError, run(["--scene", "36", "--master", "300"]));
            Assert.Equal("Blue", Commands.LightsCommand.FindScene(Profile, "37")!.Name);
            Assert.Equal(36, Commands.LightsCommand.FindScene(Profile, "red")!.Note);

            File.WriteAllText(path, """{"schema":"metrodom.light-profile/0","name":"x","fixtures":[]}""");
            Assert.Equal(MetroDomApp.UsageError, run([]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeSocket : ISacnSocket
    {
        public List<(E131Packet Packet, string Host, int Port)> Sent { get; } = [];

        public bool Fail { get; set; }

        public bool Disposed { get; private set; }

        public List<E131Packet> For(int universe) => [.. Sent.Where(s => s.Packet.Universe == universe).Select(s => s.Packet)];

        public void Send(byte[] packet, string host, int port)
        {
            if (Fail)
            {
                throw new System.Net.Sockets.SocketException(51); // network unreachable
            }

            Assert.True(E131.TryParse(packet, out var parsed));
            Sent.Add((parsed, host, port));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
