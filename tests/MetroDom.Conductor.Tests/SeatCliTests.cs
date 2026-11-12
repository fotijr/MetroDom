using MetroDom.Conductor.Cli;
using MetroDom.Core.Arranging;
using MetroDom.Core.Simulation;

namespace MetroDom.Conductor.Tests;

public class SeatCliTests
{
    [Fact]
    public void Parse_RoleAndOptionalPlayer()
    {
        Assert.True(SeatList.TryParse(["Guitar=Dom", "drums"], out var seats, out var error), error);
        Assert.Equal([new Seat("guitar", "Dom"), new Seat("drums")], seats);
    }

    [Theory]
    [InlineData("lead guitar")]
    [InlineData("=Dom")]
    [InlineData("guitar=")]
    public void Parse_RejectsBadSeats(string raw)
    {
        Assert.False(SeatList.TryParse([raw], out _, out var error));
        Assert.Contains(raw, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsTheSameSeatTwice()
    {
        Assert.False(SeatList.TryParse(["drums=Dom", "Drums=Sam"], out _, out var error));
        Assert.Contains("twice", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SaysWhatEachSeatTook()
    {
        var seated = Seating.Apply(DemoSong.Create(), [new Seat("drums", "Dom"), new Seat("bass")]);

        var lines = SeatList.Describe(seated).ToList();

        Assert.Contains("drums (Dom)", lines[0], StringComparison.Ordinal);
        Assert.Contains("Drums", lines[0], StringComparison.Ordinal);
        Assert.Contains("no bass part", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_AcceptSeats_AndRejectBadOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, MetroDomApp.Create().Run(["simulate", "--seat", "drums=Dom"], ct));
        Assert.Equal(0, MetroDomApp.Create().Run(["arrange", "-s", "melody"], ct));
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["simulate", "--seat", "lead guitar"], ct));
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["arrange", "-s", "drums", "-s", "drums"], ct));
    }
    [Fact]
    public void Parse_PiecesByNameOrNote()
    {
        Assert.True(SeatList.TryParse(["drums=Sam/kick,Snare", "drums=Ana/crash,G#3"], out var seats, out var error), error);
        Assert.Equal([35, 36, 37, 38, 39, 40], seats[0].Notes!.Order());
        Assert.Equal("drums (Sam): kick, snare", seats[0].ToString());
        Assert.Equal([49, 52, 55, 56, 57], seats[1].Notes!.Order()); // G#3 = 56
    }

    [Theory]
    [InlineData("drums=Sam/gong", "not a piece")]
    [InlineData("drums=Sam/", "no pieces")]
    public void Parse_RejectsBadPieces(string raw, string expected)
    {
        Assert.False(SeatList.TryParse([raw], out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("drums=Sam/kick", "drums=Ana/kick,snare", "share notes")]
    [InlineData("drums=Sam", "drums=Ana/crash", "twice")]
    public void Parse_RejectsSeatsThatOverlap(string first, string second, string expected)
    {
        Assert.False(SeatList.TryParse([first, second], out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_PieceSeats()
    {
        Assert.True(SeatList.TryParse(["drums=Sam/kick", "drums=Ana/cowbell"], out var seats, out _));
        var lines = SeatList.Describe(Seating.Apply(DemoSong.Create(), seats)).ToList();

        Assert.Contains("plays the rest", lines[0], StringComparison.Ordinal);
        Assert.Contains("plays those pieces", lines[1], StringComparison.Ordinal);
    }
}
