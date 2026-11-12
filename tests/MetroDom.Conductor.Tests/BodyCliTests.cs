using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Bodies;

namespace MetroDom.Conductor.Tests;

public class BodyCliTests
{
    private static string Repo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static readonly string M1 = Repo("Instruments", "metrodom_brain", "bodies", "m1-xylophone.json");

    [Fact]
    public void Writer_SendsTheRecord_AndReturnsTheBrainsReply()
    {
        var backend = new DeviceBackend();
        backend.Add("MetroDom brain", bodies: new BodyReceiver());
        var record = BodyImage.Encode(BodyFile.Load(M1));

        var reply = BodyWriter.Write(backend, backend.Outputs()[0], record, TimeSpan.FromSeconds(5));

        Assert.NotNull(reply);
        Assert.Equal(BodyWriteStatus.Written, reply.Status);
        Assert.Equal(record, BodyImage.Encode(backend.Bodies["MetroDom brain"]));
    }

    [Fact]
    public void Writer_ReturnsNull_WhenNothingAnswers()
    {
        var backend = new DeviceBackend();
        backend.Add("old firmware");

        Assert.Null(BodyWriter.Write(backend, backend.Outputs()[0], BodyImage.Encode(BodyFile.Load(M1)), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Check_AcceptsTheM1Body_AndWritesItsHex()
    {
        var hex = Path.Combine(Path.GetTempPath(), $"metrodom-body-{Guid.NewGuid():N}.hex");
        try
        {
            Assert.Equal(0, MetroDomApp.Create().Run(["body", "check", M1, "--hex", hex], TestContext.Current.CancellationToken));
            Assert.Equal(File.ReadAllText(Repo("Instruments", "metrodom_brain", "bodies", "m1-xylophone.hex")), File.ReadAllText(hex));
        }
        finally
        {
            File.Delete(hex);
        }
    }

    [Fact]
    public void Check_RefusesAnUnsafeBody_AsAUsageError()
    {
        var file = Path.Combine(Path.GetTempPath(), $"metrodom-body-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, File.ReadAllText(M1).Replace("\"maxOnMs\": 45", "\"maxOnMs\": 400", StringComparison.Ordinal));
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["body", "check", file], TestContext.Current.CancellationToken));
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["body", "check", file + ".missing"], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
