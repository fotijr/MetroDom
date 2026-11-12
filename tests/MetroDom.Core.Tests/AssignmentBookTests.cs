using MetroDom.Core.Discovery;

namespace MetroDom.Core.Tests;

public class AssignmentBookTests
{
    private static readonly DeviceIdentity Xylo = DeviceIdentity.MetroDom(1, new Version(0, 1));

    [Fact]
    public void Key_IsIdentityPlusPort_WithoutTheFirmwareVersion()
    {
        Assert.Equal("7D:224D:0001 MetroDom xylo", AssignmentBook.KeyFor("MetroDom xylo", Xylo));
        Assert.Equal(
            AssignmentBook.KeyFor("MetroDom xylo", Xylo),
            AssignmentBook.KeyFor("MetroDom xylo", DeviceIdentity.MetroDom(1, new Version(2, 0))));
        Assert.Equal("Synth", AssignmentBook.KeyFor("Synth", null));
    }

    [Fact]
    public void Find_MatchesTheKey_OrAPortOnlyAssignmentForTheSamePort()
    {
        var book = new AssignmentBook();
        book.Set(new Assignment(AssignmentBook.KeyFor("xylo", Xylo), "xylo", [0]));
        book.Set(new Assignment("quiet", "quiet", [3]));

        Assert.Equal([0], book.Find("xylo", Xylo)!.Channels);
        Assert.Null(book.Find("xylo", null)); // something else on that port that won't say what it is
        Assert.Null(book.Find("xylo", DeviceIdentity.MetroDom(2, new Version(0, 1)))); // another model
        Assert.Equal([3], book.Find("quiet", null)!.Channels);
        Assert.Equal([3], book.Find("quiet", Xylo)!.Channels); // learned to identify: keeps its role
    }

    [Fact]
    public void Set_ReplacesTheSameKey()
    {
        var book = new AssignmentBook();
        book.Set(new Assignment("a", "a", [0]));
        book.Set(new Assignment("a", "a", [5]));

        Assert.Equal([5], Assert.Single(book.All).Channels);
    }

    [Fact]
    public void Remove_ExactFirst_ThenByPartOfThePort()
    {
        var book = new AssignmentBook();
        book.Set(new Assignment("k1", "MetroDom xylo", [0]));
        book.Set(new Assignment("k2", "MetroDom drums", [9]));
        book.Set(new Assignment("k3", "Roland", [1]));

        Assert.Equal("Roland", Assert.Single(book.Remove("Roland")).Port);
        Assert.Equal(2, book.Remove("metrodom").Count);
        Assert.Empty(book.Remove("nothing"));
        Assert.Empty(book.All);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var book = new AssignmentBook();
        book.Set(new Assignment("7D:224D:0009 drums", "drums", [9], TimeSpan.FromMilliseconds(35), new HashSet<int> { 38, 36 }));
        book.Set(new Assignment("Synth", "Synth", [0, 1, 2]));

        Assert.True(AssignmentBook.TryParse(book.ToJson(), out var back, out var error), error);

        Assert.Equal(2, back.All.Count);
        var drums = back.All[0];
        Assert.Equal(("7D:224D:0009 drums", "drums"), (drums.Key, drums.Port));
        Assert.Equal([9], drums.Channels);
        Assert.Equal(TimeSpan.FromMilliseconds(35), drums.Latency);
        Assert.Equal([36, 38], drums.Notes!.Order());
        Assert.Null(back.All[1].Latency);
        Assert.Null(back.All[1].Notes);
        Assert.Contains("\"channels\": [\n        10", book.ToJson().ReplaceLineEndings("\n"), StringComparison.Ordinal); // people's numbering
    }

    [Theory]
    [InlineData("nope", "not valid")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("""{"schema":"x"}""", "not metrodom.assignments/0")]
    [InlineData("""{"schema":"metrodom.assignments/0","assignments":[{"port":"a","channels":[1]}]}""", "\"key\"")]
    [InlineData("""{"schema":"metrodom.assignments/0","assignments":[{"key":"a","port":"a","channels":[17]}]}""", "1-16")]
    [InlineData("""{"schema":"metrodom.assignments/0","assignments":[{"key":"a","port":"a","channels":[]}]}""", "1-16")]
    [InlineData("""{"schema":"metrodom.assignments/0","assignments":[{"key":"a","port":"a","channels":[1],"latencyMs":-5}]}""", "negative")]
    [InlineData("""{"schema":"metrodom.assignments/0","assignments":[{"key":"a","port":"a","channels":[1],"notes":[300]}]}""", "0-127")]
    public void Parse_RejectsBadFiles_WithAReason(string json, string reason)
    {
        Assert.False(AssignmentBook.TryParse(json, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }
}
