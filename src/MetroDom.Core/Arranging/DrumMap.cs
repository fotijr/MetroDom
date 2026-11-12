namespace MetroDom.Core.Arranging;

/// <summary>
/// General MIDI percussion facts the arranger and seats use (notes on channel 10): what a kit can
/// play instead of a sound it lacks, which pieces matter most when a chord of hits must be thinned,
/// and the piece names people use for seats ("kick", "snare", "hihat").
/// </summary>
public static class DrumMap
{
    /// <summary>
    /// For a sound the kit lacks, what to play instead, best first. Hand-picked to keep the groove:
    /// a closed hi-hat becomes the pedal or open hat, then the ride; a crash becomes another
    /// cymbal; a tom becomes the nearest tom. Notes not listed have no stand-in and are dropped.
    /// </summary>
    private static readonly Dictionary<int, int[]> Substitutes = new()
    {
        [35] = [36],
        [36] = [35],
        [37] = [38, 40, 75, 76],
        [38] = [40, 37, 39],
        [39] = [38, 40, 37],
        [40] = [38, 37, 39],
        [42] = [44, 46, 51, 59, 53, 70, 54],
        [44] = [42, 46, 51],
        [46] = [42, 44, 49, 51, 55],
        [49] = [57, 55, 52, 51, 46],
        [51] = [59, 53, 42, 46],
        [52] = [49, 57, 55],
        [53] = [51, 59, 56],
        [54] = [42, 70, 69],
        [55] = [49, 57, 52],
        [56] = [53, 76],
        [57] = [49, 55, 52],
        [59] = [51, 53],
        [69] = [70, 82, 42],
        [70] = [82, 69, 42],
        [75] = [76, 77, 37],
        [76] = [77, 75, 37],
        [77] = [76, 75, 37],
        [80] = [81, 53],
        [81] = [80, 53],
        [82] = [70, 69, 42],
    };

    /// <summary>Toms from low to high: one missing becomes the nearest the kit has.</summary>
    private static readonly int[] Toms = [41, 43, 45, 47, 48, 50];

    /// <summary>Piece names for <c>--seat drums=PLAYER/PIECES</c>, each the GM notes it covers.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> Pieces = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase)
    {
        ["kick"] = new HashSet<int> { 35, 36 },
        ["snare"] = new HashSet<int> { 37, 38, 39, 40 },
        ["hihat"] = new HashSet<int> { 42, 44, 46 },
        ["toms"] = new HashSet<int>(Toms),
        ["crash"] = new HashSet<int> { 49, 52, 55, 57 },
        ["ride"] = new HashSet<int> { 51, 53, 59 },
        ["cymbals"] = new HashSet<int> { 49, 51, 52, 53, 55, 57, 59 },
        ["cowbell"] = new HashSet<int> { 56 },
        ["tambourine"] = new HashSet<int> { 54 },
        ["percussion"] = new HashSet<int>(Enumerable.Range(60, 22).Append(54).Append(56)), // bongos to triangle, tambourine, cowbell
    };

    /// <summary>
    /// What <paramref name="kit"/> plays instead of <paramref name="note"/>, or null when nothing
    /// fits. Only called for notes the kit can't play.
    /// </summary>
    public static int? Substitute(InstrumentProfile kit, int note)
    {
        ArgumentNullException.ThrowIfNull(kit);
        if (Substitutes.TryGetValue(note, out var options) && options.FirstOrDefault(kit.CanPlay) is var pick && pick != 0)
        {
            return pick;
        }

        if (Array.IndexOf(Toms, note) >= 0)
        {
            return Toms.Where(kit.CanPlay).OrderBy(t => Math.Abs(Array.IndexOf(Toms, t) - Array.IndexOf(Toms, note))).ThenBy(t => t).Cast<int?>().FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// How much a hit matters when a kit can't strike everything at once: kick, then snare, then
    /// the timekeeping cymbals, then crashes, toms and the rest. Lower is more important.
    /// </summary>
    public static int Priority(int note) => note switch
    {
        35 or 36 => 0,
        37 or 38 or 39 or 40 => 1,
        42 or 44 or 46 or 51 or 59 => 2,
        49 or 52 or 55 or 57 => 3,
        41 or 43 or 45 or 47 or 48 or 50 => 4,
        _ => 5,
    };
}
