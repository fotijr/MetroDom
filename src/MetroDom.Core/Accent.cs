using System.Globalization;

namespace MetroDom.Core;

/// <summary>
/// An instrument's accent LED strip (ADR 0011 tier 1): its brain flashes a note's LEDs on every
/// strike, from the same Note On. See <a href="../../docs/references/body-record.md">body record</a>.
/// </summary>
/// <param name="Leds">LEDs on the strip.</param>
/// <param name="MaxBrightnessPercent">Brightest a flash gets, at velocity 127.</param>
/// <param name="FlashMs">How long a strike's LEDs stay lit.</param>
public sealed record AccentStrip(int Leds, int MaxBrightnessPercent, int FlashMs);

/// <summary>The LEDs one note flashes: <paramref name="Count"/> from <paramref name="First"/>, in one color.</summary>
/// <param name="Color">0xRRGGBB at full brightness.</param>
public sealed record AccentLed(int First, int Count, int Color)
{
    /// <summary>The color as <c>#RRGGBB</c>.</summary>
    public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{Color:X6}");

    /// <summary>Reads <c>#RRGGBB</c>.</summary>
    public static bool TryParseColor(string? text, out int color)
    {
        color = 0;
        return text is ['#', .. var digits] && digits.Length == 6
            && int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out color);
    }
}
