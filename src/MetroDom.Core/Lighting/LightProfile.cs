namespace MetroDom.Core.Lighting;

/// <summary>
/// One light (a par, a wash, an LED strip) patched into a DMX universe. <see cref="Channels"/>
/// names what each of its DMX channels does, in order, from <see cref="Address"/>. A pixel strip
/// repeats the same channels <see cref="Count"/> times.
/// </summary>
/// <param name="Id">Unique name scenes refer to.</param>
/// <param name="Universe">sACN universe, 1-63999.</param>
/// <param name="Address">First DMX channel, 1-512.</param>
/// <param name="Channels">Role of each channel: dimmer, red, green, blue, white, amber, uv, strobe, or anything else (left at 0).</param>
/// <param name="Count">Pixels (repeats of <see cref="Channels"/>); 1 for an ordinary fixture.</param>
public sealed record Fixture(string Id, int Universe, int Address, IReadOnlyList<string> Channels, int Count = 1)
{
    /// <summary>DMX channels used.</summary>
    public int Footprint => Channels.Count * Count;
}

/// <summary>A look recalled by a note: a value (0-255) per channel role, per fixture.</summary>
public sealed record LightScene(int Note, string Name, LightLook Look);

/// <summary>Where the frames go: sACN multicast for the universe, or one unicast host.</summary>
/// <param name="Destination">IP or host name for unicast; null means multicast (239.255.x.y).</param>
/// <param name="Priority">sACN priority, 0-200. Receivers take the highest.</param>
public sealed record LightOutput(string? Destination = null, int Priority = LightOutput.DefaultPriority)
{
    public const int DefaultPriority = 100;
}

/// <summary>
/// A light rig, described like an instrument (ADR 0011 §2): notes recall scenes, a CC is the
/// master dimmer, and its latency is measured so light lands with sound.
/// See <a href="../../../docs/references/light-profile.md">light profile</a>.
/// </summary>
public sealed record LightProfile(string Name, IReadOnlyList<Fixture> Fixtures)
{
    /// <summary>Default lights channel (16, zero-based 15).</summary>
    public const int DefaultChannel = 15;

    /// <summary>Default master dimmer controller.</summary>
    public const int DefaultMasterCc = 7;

    /// <summary>MIDI channel (0-15) the rig listens on.</summary>
    public int Channel { get; init; } = DefaultChannel;

    /// <summary>Controller number (0-119) for the master dimmer.</summary>
    public int MasterCc { get; init; } = DefaultMasterCc;

    /// <summary>Command → visible change (network, node, fixture). Lets the scheduler send light early like sound.</summary>
    public TimeSpan Latency { get; init; }

    public IReadOnlyList<LightScene> Scenes { get; init; } = [];

    /// <summary>What panic and stopping show: steady and dim, never dark, never flashing (ADR 0011 §5).</summary>
    public LightLook SafeLook { get; init; } = LightLook.DefaultSafe;

    public LightOutput Output { get; init; } = new();

    /// <summary>How the rig follows the band when no cue says otherwise.</summary>
    public ReactiveSettings Reactive { get; init; } = new();

    /// <summary>Every universe the rig uses, in order.</summary>
    public IReadOnlyList<int> Universes => [.. Fixtures.Select(f => f.Universe).Distinct().Order()];

    /// <summary>The scene for <paramref name="note"/>, or null.</summary>
    public LightScene? SceneFor(int note) => Scenes.FirstOrDefault(s => s.Note == note);
}

/// <summary>
/// Channel values per fixture. <c>"*"</c> applies to every fixture; a fixture's own entry wins
/// for the roles it names. A fixture or role not mentioned is 0, so a look is a whole picture.
/// </summary>
public sealed class LightLook
{
    /// <summary>Applies to every fixture.</summary>
    public const string AllFixtures = "*";

    /// <summary>Warm, dim, steady: what panic shows when the rig doesn't say.</summary>
    public static readonly LightLook DefaultSafe = new(new Dictionary<string, IReadOnlyDictionary<string, byte>>
    {
        [AllFixtures] = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["dimmer"] = 160,
            ["red"] = 160,
            ["green"] = 120,
            ["blue"] = 80,
            ["white"] = 100,
            ["amber"] = 100,
        },
    });

    /// <summary>Everything at 0.</summary>
    public static readonly LightLook Blackout = new(new Dictionary<string, IReadOnlyDictionary<string, byte>>());

    public LightLook(IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte>> values) => Values = values;

    /// <summary>Fixture id (or <c>"*"</c>) → role → value.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte>> Values { get; }

    /// <summary>The value for <paramref name="role"/> on <paramref name="fixture"/>.</summary>
    public byte ValueFor(string fixture, string role) =>
        Values.TryGetValue(fixture, out var own) && TryGet(own, role, out var value) ? value
        : Values.TryGetValue(AllFixtures, out var all) && TryGet(all, role, out value) ? value
        : (byte)0;

    private static bool TryGet(IReadOnlyDictionary<string, byte> roles, string role, out byte value)
    {
        foreach (var (name, v) in roles)
        {
            if (string.Equals(name, role, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = 0;
        return false;
    }
}

/// <summary>Turns a look into DMX frames, one 512-slot array per universe.</summary>
public static class LightRenderer
{
    public const int SlotsPerUniverse = 512;

    /// <summary>
    /// Fills every universe the rig uses. <paramref name="master"/> (0-255) scales intensity
    /// channels. Strobe channels are always 0: a fixture's built-in strobe flashes faster than the
    /// 3-per-second cap, and nothing yet lets a venue's policy allow it (ADR 0011 §5).
    /// </summary>
    public static IReadOnlyDictionary<int, byte[]> Render(LightProfile profile, LightLook look, byte master = 255)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(look);
        var frames = profile.Universes.ToDictionary(u => u, _ => new byte[SlotsPerUniverse]);
        foreach (var fixture in profile.Fixtures)
        {
            var frame = frames[fixture.Universe];
            for (var pixel = 0; pixel < fixture.Count; pixel++)
            {
                for (var i = 0; i < fixture.Channels.Count; i++)
                {
                    var role = fixture.Channels[i];
                    var value = IsStrobe(role) ? 0 : look.ValueFor(fixture.Id, role);
                    if (IsIntensity(role))
                    {
                        value = (byte)((value * master + 127) / 255);
                    }

                    frame[fixture.Address - 1 + (pixel * fixture.Channels.Count) + i] = (byte)value;
                }
            }
        }

        return frames;
    }

    /// <summary>Roles the master dimmer scales: anything that makes light.</summary>
    public static bool IsIntensity(string role) =>
        role.ToUpperInvariant() is "DIMMER" or "RED" or "GREEN" or "BLUE" or "WHITE" or "AMBER" or "UV";

    public static bool IsStrobe(string role) => string.Equals(role, "strobe", StringComparison.OrdinalIgnoreCase);
}
