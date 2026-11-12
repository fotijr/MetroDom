using System.Globalization;

namespace MetroDom.Core.Lighting;

/// <summary>A color, 0-255 per primary.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#rrggbb</c>.</summary>
    public static bool TryParse(string? text, out Rgb rgb)
    {
        rgb = default;
        if (text is not ['#', _, _, _, _, _, _]
            || !int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        rgb = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>
/// How reactive lights look (ADR 0011 §3.1): an idle glow, a pulse per note that fades, a color
/// per MIDI channel, and which channels each fixture follows.
/// </summary>
public sealed record ReactiveSettings
{
    /// <summary>
    /// One color per MIDI channel (0-15): distinct hues, drums (10) amber, so each instrument
    /// is recognizable.
    /// </summary>
    public static readonly IReadOnlyList<Rgb> DefaultPalette =
    [
        new(0x2F, 0x80, 0xFF), // 1 blue
        new(0xFF, 0x3B, 0x30), // 2 red
        new(0x30, 0xD1, 0x58), // 3 green
        new(0xBF, 0x5A, 0xF2), // 4 purple
        new(0x00, 0xC7, 0xBE), // 5 teal
        new(0xFF, 0x2D, 0x95), // 6 pink
        new(0xFF, 0xD6, 0x0A), // 7 yellow
        new(0x5E, 0x5C, 0xE6), // 8 indigo
        new(0xFF, 0x9F, 0x0A), // 9 orange
        new(0xFF, 0xB0, 0x00), // 10 drums: amber
        new(0x64, 0xD2, 0xFF), // 11 cyan
        new(0xA2, 0x84, 0x5E), // 12 brown
        new(0xFF, 0x64, 0x82), // 13 rose
        new(0x8E, 0xE5, 0x3F), // 14 lime
        new(0xC0, 0xC0, 0xFF), // 15 lavender
        new(0xFF, 0xFF, 0xFF), // 16 lights channel (not pulsed)
    ];

    /// <summary>Color of a fixture that follows every channel.</summary>
    public static readonly Rgb Neutral = new(0xFF, 0xC8, 0x96);

    /// <summary>Glow between notes, 0-255. Never dark, so pulses aren't dark-to-bright flashes.</summary>
    public byte BaseLevel { get; init; } = 40;

    /// <summary>How long a pulse takes to fade back to the glow.</summary>
    public TimeSpan Decay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Color per MIDI channel (0-15).</summary>
    public IReadOnlyList<Rgb> Colors { get; init; } = DefaultPalette;

    /// <summary>Fixture id → MIDI channels (0-15) it follows. Fixtures not listed are shared out across the band.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<int>> Follow { get; init; } = new Dictionary<string, IReadOnlySet<int>>();
}

/// <summary>
/// Reactive mode: turns the note stream into a look, so every song gets a light show for free.
/// Each fixture follows some channels; a note on one of them starts a pulse whose peak follows
/// velocity and which fades back to the idle glow, in that channel's color. Notes are given
/// with the time their light should change (sound time minus the rig's latency), which may be in
/// the future. <b>Flash cap:</b> a fixture starts at most 3 pulses per second, and notes inside
/// that window change nothing on it (ADR 0011 §5). Thread-safe.
/// </summary>
public sealed class ReactiveLights
{
    /// <summary>Shortest time between two pulses on one fixture: 3 flashes per second at most.</summary>
    public static readonly TimeSpan MinPulseGap = TimeSpan.FromSeconds(1.0 / 3);

    private readonly LightProfile _profile;
    private readonly ReactiveSettings _settings;
    private readonly FixtureState[] _fixtures;
    private readonly List<(TimeSpan At, int Channel, int Velocity)> _pending = [];
    private readonly Lock _gate = new();

    /// <param name="profile">The rig.</param>
    /// <param name="band">Each instrument's channels (0-15). Fixtures the profile doesn't assign are shared out across these in turn; with no band they follow every channel.</param>
    public ReactiveLights(LightProfile profile, IReadOnlyList<IReadOnlyList<int>> band)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(band);
        _profile = profile;
        _settings = profile.Reactive;
        var members = band.Where(m => m.Count > 0).ToList();
        var next = 0;
        _fixtures = [.. profile.Fixtures.Select(f =>
        {
            IReadOnlySet<int>? follows = _settings.Follow.TryGetValue(f.Id, out var given) ? given
                : members.Count > 0 ? members[next++ % members.Count].ToHashSet()
                : null;
            follows = follows?.Where(c => c != profile.Channel).ToHashSet(); // the lights channel is for cues, not pulses
            var idle = follows is { Count: > 0 } ? _settings.Colors[follows.Min()] : ReactiveSettings.Neutral;
            return new FixtureState(f, follows, idle);
        })];
    }

    /// <summary>Which channels each fixture follows (null: every channel), for showing the plan.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<int>?> Following => _fixtures.ToDictionary(f => f.Fixture.Id, f => f.Follows);

    /// <summary>A note whose light should change at <paramref name="at"/>. Velocity 0 is a note off and ignored.</summary>
    public void Note(int channel, int velocity, TimeSpan at)
    {
        if (velocity <= 0 || channel == _profile.Channel)
        {
            return;
        }

        lock (_gate)
        {
            _pending.Add((at, channel, velocity));
        }
    }

    /// <summary>Back to the idle glow: pulses and notes not yet shown are dropped (panic, a new song).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            foreach (var f in _fixtures)
            {
                f.Reset();
            }
        }
    }

    /// <summary>The look at <paramref name="now"/>.</summary>
    public LightLook Look(TimeSpan now)
    {
        lock (_gate)
        {
            if (_pending.Count > 0)
            {
                // Sends arrive in send order, which differs by instrument latency: show in light order.
                var due = _pending.Where(p => p.At <= now).OrderBy(p => p.At).ToList();
                _pending.RemoveAll(p => p.At <= now);
                foreach (var (at, channel, velocity) in due)
                {
                    foreach (var f in _fixtures.Where(f => f.Follows is null || f.Follows.Contains(channel)))
                    {
                        if (f.LastPulse is { } last && at - last < MinPulseGap)
                        {
                            continue;
                        }

                        f.LastPulse = at;
                        f.Peak = Math.Clamp(velocity, 1, 127) / 127.0;
                        f.Color = _settings.Colors[channel];
                    }
                }
            }

            var values = new Dictionary<string, IReadOnlyDictionary<string, byte>>(StringComparer.Ordinal);
            foreach (var f in _fixtures)
            {
                values[f.Fixture.Id] = Roles(f, Level(f, now));
            }

            return new LightLook(values);
        }
    }

    /// <summary>0-255: the glow plus what's left of the pulse (linear fade).</summary>
    private byte Level(FixtureState f, TimeSpan now)
    {
        var glow = _settings.BaseLevel;
        if (f.LastPulse is not { } start || _settings.Decay <= TimeSpan.Zero)
        {
            return glow;
        }

        var left = 1 - ((now - start) / _settings.Decay);
        return left <= 0 ? glow : (byte)Math.Round(glow + ((255 - glow) * f.Peak * left));
    }

    /// <summary>A fixture with a dimmer gets full color and the level on the dimmer; one without gets the color scaled.</summary>
    private static Dictionary<string, byte> Roles(FixtureState f, byte level)
    {
        var hasDimmer = f.Fixture.Channels.Any(c => string.Equals(c, "dimmer", StringComparison.OrdinalIgnoreCase));
        byte Scale(byte v) => hasDimmer ? v : (byte)((v * level + 127) / 255);
        return new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["dimmer"] = level,
            ["red"] = Scale(f.Color.R),
            ["green"] = Scale(f.Color.G),
            ["blue"] = Scale(f.Color.B),
        };
    }

    private sealed class FixtureState(Fixture fixture, IReadOnlySet<int>? follows, Rgb idle)
    {
        public Fixture Fixture { get; } = fixture;

        public IReadOnlySet<int>? Follows { get; } = follows;

        public Rgb Idle { get; } = idle;

        public Rgb Color { get; set; } = idle;

        public TimeSpan? LastPulse { get; set; }

        public double Peak { get; set; }

        public void Reset()
        {
            Color = Idle;
            LastPulse = null;
            Peak = 0;
        }
    }
}
