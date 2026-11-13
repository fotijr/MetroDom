using MetroDom.Core.Arranging;

namespace MetroDom.Core.Tests;

public class DrumMapTests
{
    private static InstrumentProfile Kit(params int[] notes) => new("kit", TimeSpan.FromMilliseconds(30)) { Notes = notes.ToHashSet() };

    [Theory]
    [InlineData(42, new[] { 36, 46, 51 }, 46)] // closed hat → open hat before the ride
    [InlineData(42, new[] { 36, 51 }, 51)]
    [InlineData(37, new[] { 38 }, 38)]
    [InlineData(57, new[] { 49, 51 }, 49)]
    [InlineData(43, new[] { 41, 48 }, 41)] // nearest tom down…
    [InlineData(47, new[] { 41, 50 }, 50)] // …or up
    [InlineData(45, new[] { 43, 47 }, 43)] // a tie goes to the lower tom
    public void Substitute_PicksTheBestStandInTheKitHas(int note, int[] kit, int expected) =>
        Assert.Equal(expected, DrumMap.Substitute(Kit(kit), note));

    [Theory]
    [InlineData(81, new[] { 36, 38 })] // triangle: nothing like it
    [InlineData(42, new[] { 36, 38 })] // no cymbal at all
    [InlineData(45, new[] { 36 })] // no toms at all
    public void Substitute_IsNullWhenNothingFits(int note, int[] kit) =>
        Assert.Null(DrumMap.Substitute(Kit(kit), note));

    private static readonly int[] OneOfEach = [36, 38, 42, 51, 49, 45, 81];

    [Fact]
    public void Priority_KickSnareTimeCrashTomsRest() =>
        Assert.Equal([0, 1, 2, 2, 3, 4, 5], OneOfEach.Select(DrumMap.Priority));

    [Fact]
    public void Pieces_AreCaseInsensitive_AndCoverTheKit()
    {
        Assert.Equal([35, 36], DrumMap.Pieces["Kick"].Order());
        Assert.Contains(46, DrumMap.Pieces["hihat"]);
        Assert.True(DrumMap.Pieces["cymbals"].IsSupersetOf(DrumMap.Pieces["ride"]));
    }
}
