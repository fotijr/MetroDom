using System.Globalization;
using MetroDom.Core.Bodies;

namespace MetroDom.Core.Tests;

/// <summary>
/// <see cref="BrainModel"/> decides what the brain firmware decides. The parity vectors in
/// Instruments/metrodom_brain/test/vectors run here and in the firmware's host test.
/// </summary>
public class BrainModelTests
{
    private static string BrainPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir!.FullName, "Instruments", "metrodom_brain", .. parts]);
    }

    private static BodyDefinition M1()
    {
        Assert.True(BodyImage.TryParseJson(File.ReadAllText(BrainPath("bodies", "m1-xylophone.json")), out var body, out var error), error);
        return body;
    }

    public static TheoryData<string> Vectors() =>
        [.. Directory.EnumerateFiles(BrainPath("test", "vectors"), "*.vectors").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Vectors_MatchTheFirmware(string name)
    {
        var path = BrainPath("test", "vectors", name);
        BrainModel? brain = null;
        var failures = new List<string>();
        var number = 0;
        foreach (var raw in File.ReadLines(path))
        {
            number++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t[0] == "body")
            {
                var hex = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, t[1])).Trim();
                Assert.True(BodyImage.TryDecode(Convert.FromHexString(hex), out var body, out var error), error);
                brain = new BrainModel(body);
                continue;
            }

            Assert.NotNull(brain);
            var now = long.Parse(t[0], CultureInfo.InvariantCulture);
            brain.Tick(now);
            switch (t[1])
            {
                case "panic":
                    brain.AllOff();
                    break;
                case "is-on" or "is-lit":
                    var note = int.Parse(t[2], CultureInfo.InvariantCulture);
                    var got = t[1] == "is-on" ? brain.IsOn(note) : brain.IsLit(note);
                    if (got != (t[3] == "yes"))
                    {
                        failures.Add($"{name}:{number}: {line}: got {(got ? "yes" : "no")}");
                    }

                    break;
                case "on":
                    var strike = brain.NoteOn(0, int.Parse(t[2], CultureInfo.InvariantCulture), int.Parse(t[3], CultureInfo.InvariantCulture), now);
                    var outcome = strike is null ? "none" : strike.Struck ? "struck" : strike.Refusal.ToString().ToLowerInvariant();
                    if (outcome != t[4])
                    {
                        failures.Add($"{name}:{number}: {line}: got {outcome}");
                    }
                    else if (strike is { Struck: true })
                    {
                        if (strike.PulseMs != int.Parse(t[5], CultureInfo.InvariantCulture))
                        {
                            failures.Add($"{name}:{number}: {line}: pulse {strike.PulseMs}");
                        }

                        var flash = strike.Flash is { } c ? string.Create(CultureInfo.InvariantCulture, $"#{c:X6}") : "none";
                        if (t.Length > 6 && flash != t[6])
                        {
                            failures.Add($"{name}:{number}: {line}: flashed {flash}");
                        }
                    }

                    break;
                default:
                    failures.Add($"{name}:{number}: unknown line: {line}");
                    break;
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Vectors_UseTheCommittedFixtures()
    {
        // The vectors name hex fixtures; the Conductor's encoder must make those same bytes from the JSON.
        foreach (var name in new[] { "m1-xylophone", "m1-xylophone-accent" })
        {
            Assert.True(BodyImage.TryParseJson(File.ReadAllText(BrainPath("bodies", name + ".json")), out var body, out _));
            Assert.Equal(File.ReadAllText(BrainPath("bodies", name + ".hex")).Trim(), BodyImage.ToHex(BodyImage.Encode(body)));
        }
    }

    [Fact]
    public void Counts_StrikesAndRefusalsByReason()
    {
        var brain = new BrainModel(M1());

        brain.NoteOn(0, 72, 100, 0);
        brain.NoteOn(0, 72, 100, 10);
        brain.NoteOn(0, 60, 100, 20);
        brain.NoteOn(0, 72, 100, 40);

        Assert.Equal(1, brain.Struck);
        Assert.Equal(1, brain.Refused[BrainRefusal.Busy]);
        Assert.Equal(1, brain.Refused[BrainRefusal.NotMine]);
        Assert.Equal(1, brain.Refused[BrainRefusal.Interval]);
    }

    [Fact]
    public void Channel_OnlyItsOwn_WhenTheBodyHasOne()
    {
        var brain = new BrainModel(M1() with { Channel = 2 });

        Assert.Equal(BrainRefusal.NotMine, brain.NoteOn(0, 72, 100, 0)!.Refusal);
        Assert.True(brain.NoteOn(2, 72, 100, 0)!.Struck);
    }

    [Fact]
    public void Ceilings_ClampABodyThatSlippedPastValidation()
    {
        var body = M1() with
        {
            MaxOnMs = 1000,
            Notes = [new BodyActuator(72, 0, 900, 0, 900, 0)],
        };

        Assert.Equal(BodyImage.CeilingOnMs, new BrainModel(body).PulseFor(72, 127));
    }

    [Fact]
    public void Refusal_SaysWhyInWords()
    {
        var brain = new BrainModel(M1());
        brain.NoteOn(0, 72, 100, 0);

        Assert.Equal("still striking", brain.NoteOn(0, 72, 100, 10)!.Why);
        Assert.Null(brain.NoteOn(0, 74, 100, 10)!.Why);
    }
}
