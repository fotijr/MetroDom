using System.Globalization;
using System.Text;

namespace MetroDom.Core.Timing;

/// <summary>
/// A player's hits as text (docs/references/beat-following.md): one hit per line, its time in
/// seconds as the first comma-separated field. Other fields (note, velocity) are kept for people
/// and ignored here. Blank lines, <c>#</c> comments and a header line are skipped.
/// </summary>
public static class HitLog
{
    /// <summary>The header <see cref="Write"/> puts first.</summary>
    public const string Header = "seconds,note,velocity";

    /// <summary>Reads hits, sorted, or returns false with the first bad line.</summary>
    public static bool TryParse(string text, out IReadOnlyList<TimeSpan> hits, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        var list = new List<TimeSpan>();
        hits = list;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var field = line.Split(',')[0].Trim();
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0)
            {
                if (list.Count == 0 && char.IsLetter(field.FirstOrDefault()))
                {
                    continue; // A header.
                }

                error = $"line {i + 1}: '{field}' is not a time in seconds.";
                return false;
            }

            list.Add(TimeSpan.FromSeconds(seconds));
        }

        list.Sort();
        error = list.Count == 0 ? "no hits." : null;
        return list.Count > 0;
    }

    /// <summary>Hits as text, times relative to the first hit.</summary>
    public static string Write(IReadOnlyList<(TimeSpan At, int Note, int Velocity)> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var text = new StringBuilder(Header).Append('\n');
        var start = hits.Count > 0 ? hits[0].At : TimeSpan.Zero;
        foreach (var (at, note, velocity) in hits)
        {
            text.Append(CultureInfo.InvariantCulture, $"{(at - start).TotalSeconds:0.0000},{note},{velocity}\n");
        }

        return text.ToString();
    }
}
