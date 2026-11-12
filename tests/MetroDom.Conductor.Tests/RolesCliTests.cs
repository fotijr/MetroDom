using MetroDom.Conductor.Cli;
using MetroDom.Core.Arranging;
using MetroDom.Core.Simulation;

namespace MetroDom.Conductor.Tests;

public class RolesCliTests
{
    [Fact]
    public void Set_SavesNextToTheSong_AndUnsetForgets()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var song = Path.Combine(dir.FullName, "demo.mid");
            DemoSong.Create().Write(song);
            var ct = TestContext.Current.CancellationToken;

            Assert.Equal(0, MetroDomApp.Create().Run(["roles", song, "--set", "Xylophone=Lead", "--set", "#2=Percussion"], ct));
            var saved = SongRoles.Load(song)!;
            Assert.Equal("lead", saved.Parts["Xylophone"]);
            Assert.Equal("percussion", saved.Parts["#2"]);

            Assert.Equal(0, MetroDomApp.Create().Run(["roles", song, "--unset", "#2"], ct));
            Assert.False(SongRoles.Load(song)!.Parts.ContainsKey("#2"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("Xylophone")]
    [InlineData("=melody")]
    [InlineData("Xylophone=lead guitar")]
    public void Set_RejectsBadValues(string value)
    {
        var song = Path.GetTempFileName();
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["roles", song, "--set", value], TestContext.Current.CancellationToken));
            Assert.False(File.Exists(RoleMapping.PathFor(song)));
        }
        finally
        {
            File.Delete(song);
        }
    }

    [Fact]
    public void BrokenRolesFile_IsAUsageError_NamingTheFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var song = Path.Combine(dir.FullName, "demo.mid");
            DemoSong.Create().Write(song);
            File.WriteAllText(RoleMapping.PathFor(song), "{}");

            var ex = Assert.Throws<UsageException>(() => SongRoles.Load(song));
            Assert.Contains("demo.roles.json", ex.Message, StringComparison.Ordinal);
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["arrange", song], TestContext.Current.CancellationToken));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
