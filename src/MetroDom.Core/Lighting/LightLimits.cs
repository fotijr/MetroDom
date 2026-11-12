using System.Globalization;

namespace MetroDom.Core.Lighting;

/// <summary>
/// A stretch of the day with tighter light limits: a brightness cap and no strobe. <see cref="From"/>
/// after <see cref="To"/> wraps past midnight (23:00-07:00).
/// </summary>
/// <param name="From">Local time it starts.</param>
/// <param name="To">Local time it ends (not included).</param>
/// <param name="MaxBrightness">Brightness cap in percent, 10-100.</param>
public sealed record QuietHours(TimeOnly From, TimeOnly To, int MaxBrightness)
{
    /// <summary>Whether <paramref name="time"/> falls in the quiet hours.</summary>
    public bool Contains(TimeOnly time) => From <= To
        ? time >= From && time < To
        : time >= From || time < To;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{From:HH\\:mm}-{To:HH\\:mm} at {MaxBrightness}%");
}

/// <summary>
/// What a venue allows the lights to do (policy profile <c>lights</c>, ADR 0011 §5, ADR 0005):
/// how often the look may flash, whether fixtures' strobe channels may be used, a brightness cap,
/// and quiet hours. The defaults are the safe ones: 3 flashes a second, no strobe, full brightness.
/// </summary>
public sealed record LightLimits
{
    /// <summary>The photosensitive-epilepsy guideline: 3 flashes a second.</summary>
    public const double DefaultFlashesPerSecond = 3;

    /// <summary>The fastest rate any policy may set, and only with <see cref="Strobe"/> on.</summary>
    public const double StrobeFlashesPerSecond = 10;

    /// <summary>The lowest brightness cap: the safe look must stay visible.</summary>
    public const int MinBrightness = 10;

    /// <summary>The defaults: no strobe, at most 3 flashes a second, full brightness, no quiet hours.</summary>
    public static LightLimits Default { get; } = new();

    /// <summary>Whether fixtures' own <c>strobe</c> channels may be set (scenes only). Off by default.</summary>
    public bool Strobe { get; init; }

    /// <summary>Most look changes per second: pulses per fixture, scene changes, master swings. Above 3 needs <see cref="Strobe"/>.</summary>
    public double MaxFlashesPerSecond { get; init; } = DefaultFlashesPerSecond;

    /// <summary>Brightness cap in percent (10-100), applied to every intensity channel.</summary>
    public int MaxBrightness { get; init; } = 100;

    /// <summary>Tighter limits for part of the day, if any.</summary>
    public QuietHours? Quiet { get; init; }

    /// <summary>Shortest time between two flashes.</summary>
    public TimeSpan FlashGap => TimeSpan.FromSeconds(1 / MaxFlashesPerSecond);

    /// <summary>Whether these are the defaults (nothing to say about them).</summary>
    public bool IsDefault => this == Default;

    /// <summary>Brightness cap at local <paramref name="time"/>, 0-255 (the lower of the two caps in quiet hours).</summary>
    public byte BrightnessAt(TimeOnly time)
    {
        var percent = Quiet is { } quiet && quiet.Contains(time) ? Math.Min(MaxBrightness, quiet.MaxBrightness) : MaxBrightness;
        return (byte)Math.Round(percent * 255 / 100.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Whether strobe channels may be set at local <paramref name="time"/>: never in quiet hours.</summary>
    public bool StrobeAt(TimeOnly time) => Strobe && Quiet?.Contains(time) != true;

    /// <summary>One line for the console: "≤ 3 flashes/s, no strobe, brightness 80%, quiet 23:00-07:00 at 30%".</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"≤ {MaxFlashesPerSecond:0.#} flashes/s, {(Strobe ? "strobe allowed" : "no strobe")}, brightness {MaxBrightness}%{(Quiet is { } q ? $", quiet {q}" : "")}");

    /// <summary>Checks the limits, or returns a reason a person can act on.</summary>
    public string? Validate() =>
        MaxFlashesPerSecond is <= 0 or > StrobeFlashesPerSecond ? $"\"lights.maxFlashesPerSecond\" must be above 0 and at most {StrobeFlashesPerSecond}."
        : MaxFlashesPerSecond > DefaultFlashesPerSecond && !Strobe ? $"\"lights.maxFlashesPerSecond\" above {DefaultFlashesPerSecond} needs \"lights.strobe\": true."
        : MaxBrightness is < MinBrightness or > 100 ? $"\"lights.maxBrightness\" must be {MinBrightness}-100 (percent)."
        : Quiet is { MaxBrightness: < MinBrightness or > 100 } ? $"\"lights.quietHours.maxBrightness\" must be {MinBrightness}-100 (percent)."
        : Quiet is { } q && q.From == q.To ? "\"lights.quietHours\": from and to must differ."
        : null;
}
