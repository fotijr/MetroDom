using MetroDom.Conductor;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Core.Bodies;

namespace MetroDom.Conductor.Tests;

public class InstrumentCliTests
{
    private static InstrumentScaffold.Files Build(string notes, BodyDriver driver = BodyDriver.Pca9685)
    {
        Assert.True(NoteList.TryParse(notes, out var parsed, out _));
        return InstrumentScaffold.Build(
            new("snare", "MetroDom snare", parsed, driver, "striker-solenoid", "drum-kit"),
            InstrumentScaffold.ReadmeTemplate());
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"metrodom-inst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Scaffold_MakesABodyABrainAccepts_OneOutputPerNote()
    {
        var files = Build("C5-E5");

        Assert.Null(BodyImage.Validate(files.Body));
        Assert.Equal([72, 73, 74, 75, 76], files.Body.Notes.Select(n => n.Note));
        Assert.Equal([0, 1, 2, 3, 4], files.Body.Notes.Select(n => n.Output));
        Assert.Equal(2, files.Body.Polyphony);
        Assert.True(BodyImage.TryParseJson(files.BodyJson, out var back, out _));
        Assert.Equal(BodyImage.Encode(files.Body), BodyImage.Encode(back));
    }

    [Fact]
    public void Scaffold_ListsAnActuatorPerNote_AndADriverPerEight()
    {
        var toml = Build("60-68").Toml;

        Assert.Contains("id = \"striker-solenoid\"\nqty = 9", toml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("id = \"driver-mosfet-8ch\"\nqty = 2", toml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("plays = \"drum-kit\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_GpioBody_KeepsOffTheBackplaneAndLedPins()
    {
        var body = Build("60-75", BodyDriver.Gpio).Body;

        Assert.Null(BodyImage.Validate(body));
        Assert.DoesNotContain(body.Notes, n => BodyImage.ReservedPins.Contains(n.Output) || n.Output == BodyImage.LedPin);
    }

    [Fact]
    public void Scaffold_RefusesMoreNotesThanABodyHolds()
    {
        Assert.Throws<UsageException>(() => Build("60-76"));
    }

    [Fact]
    public void Scaffold_FillsTheReadme()
    {
        var readme = Build("38").Readme;

        Assert.StartsWith("# MetroDom snare", readme, StringComparison.Ordinal);
        Assert.Contains("| D2 (38) | 0 | striker-solenoid |", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void New_WritesTheFolder_AndRefusesToOverwriteIt()
    {
        var dir = TempDir();
        try
        {
            string[] args = ["instrument", "new", "snare", "--notes", "38", "--plays", "drum-kit", "--dir", dir];
            Assert.Equal(0, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(dir, "snare", "instrument.toml")));
            Assert.True(File.Exists(Path.Combine(dir, "snare", "README.md")));
            Assert.True(BodyImage.TryParseJson(File.ReadAllText(Path.Combine(dir, "snare", "body.json")), out var body, out _));
            Assert.Equal("MetroDom snare", body.Name);

            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("--actuator", "flux-capacitor")]
    [InlineData("--plays", "banjo")]
    [InlineData("--driver", "relay")]
    [InlineData("--notes", "H9")]
    public void New_RefusesWhatTheRepoDoesntKnow_AndWritesNothing(string option, string value)
    {
        var dir = TempDir();
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["instrument", "new", "snare", option, value, "--dir", dir], TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BodyCheck_WithNoFile_AcceptsEveryInstrumentBodyInTheRepo()
    {
        var root = Workspace.FindRoot();
        Assert.NotNull(root);
        Assert.Contains(Workspace.InstrumentBodies(root), f => f.Contains("_template", StringComparison.Ordinal));
        Assert.Equal(0, MetroDomApp.Create().Run(["body", "check"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BodyCheck_HexNeedsAFile()
    {
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["body", "check", "--hex", "out.hex"], TestContext.Current.CancellationToken));
    }
}
