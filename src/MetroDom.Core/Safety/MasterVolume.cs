namespace MetroDom.Core.Safety;

/// <summary>
/// The band's volume knob, 0-100%, set live from the remote or the keyboard. Each instrument's
/// <see cref="SafetyGovernor"/> scales new notes' velocity by it, before the policy's caps, so it
/// can only make the band quieter than the limits allow, never louder. 0 holds back every new
/// note (what's sounding still ends). Thread-safe.
/// </summary>
public sealed class MasterVolume
{
    /// <summary>Where the knob starts: the song as written, under the policy's caps.</summary>
    public const int Full = 100;

    private int _percent = Full;

    /// <summary>The level now, 0-100.</summary>
    public int Percent => Volatile.Read(ref _percent);

    /// <summary>Sets the level. False (and unchanged) outside 0-100.</summary>
    public bool TrySet(int percent)
    {
        if (percent is < 0 or > Full)
        {
            return false;
        }

        Volatile.Write(ref _percent, percent);
        return true;
    }

    /// <summary>
    /// <paramref name="velocity"/> at this level: 0 when muted, else at least 1, so a quiet song
    /// at a low level still sounds rather than vanishing note by note.
    /// </summary>
    public int Scale(int velocity)
    {
        var percent = Percent;
        return percent == 0 || velocity <= 0 ? 0 : Math.Max(1, (int)Math.Round(velocity * percent / (double)Full, MidpointRounding.AwayFromZero));
    }
}
