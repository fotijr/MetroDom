using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.MusicTheory;

namespace MetroDom.Conductor.Cli;

/// <summary>
/// Note sets as written on the command line: numbers or names (middle C = 60 = C4), comma
/// separated, with ranges: <c>C4-C6</c>, <c>36,38,76</c>, <c>60-72,F#5</c>.
/// </summary>
internal static class NoteList
{
    public static bool TryParse(string raw, out IReadOnlySet<int> notes, out string? error)
    {
        var parsed = new SortedSet<int>();
        notes = parsed;
        error = null;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Split a range on the '-' between two notes, not a sign: "C4-C6", "60-72".
            var dash = part.IndexOf('-', 1);
            var first = dash < 0 ? part : part[..dash];
            var last = dash < 0 ? part : part[(dash + 1)..];
            if (!TryNote(first, out var low) || !TryNote(last, out var high) || high < low)
            {
                error = $"'{part}' is not a note (0-127 or a name like C4, F#3) or range (C4-C6).";
                return false;
            }

            for (var n = low; n <= high; n++)
            {
                parsed.Add(n);
            }
        }

        if (parsed.Count == 0)
        {
            error = "no notes given.";
            return false;
        }

        return true;
    }

    /// <summary>Formats a note set compactly: runs as ranges by number ("60-72,76").</summary>
    public static string Format(IReadOnlySet<int>? notes)
    {
        if (notes is null)
        {
            return "any";
        }

        var sorted = notes.Order().ToArray();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Length;)
        {
            var end = i;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
            {
                end++;
            }

            parts.Add(end > i ? string.Create(CultureInfo.InvariantCulture, $"{sorted[i]}-{sorted[end]}") : sorted[i].ToString(CultureInfo.InvariantCulture));
            i = end + 1;
        }

        return string.Join(",", parts);
    }

    /// <summary>Note name for display: 60 → "C4".</summary>
    public static string Name(int number) => Note.Get((SevenBitNumber)(byte)Math.Clamp(number, 0, 127)).ToString();

    private static bool TryNote(string text, out int number)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return number is >= 0 and <= 127;
        }

        if (Note.TryParse(text, out var note))
        {
            number = note.NoteNumber;
            return true;
        }

        return false;
    }
}
