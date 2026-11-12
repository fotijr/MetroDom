using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MetroDom.Core.Arranging;

/// <summary>
/// The parts of a band a song track can be (ADR 0012): what a seat takes and the arranger leaves
/// out. Detection only ever produces these; a saved <see cref="RoleMapping"/> may use any word.
/// </summary>
public static class Roles
{
    public const string Drums = "drums", Bass = "bass", Guitar = "guitar", Keys = "keys", Strings = "strings",
        Brass = "brass", Winds = "winds", Vocals = "vocals", Melody = "melody", Other = "other";

    /// <summary>Every role detection can produce, in display order.</summary>
    public static readonly IReadOnlyList<string> All = [Drums, Bass, Guitar, Keys, Strings, Brass, Winds, Vocals, Melody, Other];

    /// <summary>A role as written by a person: trimmed, lower case. Empty or blank is not a role.</summary>
    public static bool TryNormalize(string? raw, out string role)
    {
        role = raw?.Trim().ToLowerInvariant() ?? "";
        return role.Length > 0 && !role.Any(char.IsWhiteSpace);
    }
}

/// <summary>Where a part's role came from, strongest first.</summary>
public enum RoleSource
{
    /// <summary>The song's saved mapping (<c>song.roles.json</c>).</summary>
    Mapping,

    /// <summary>MIDI channel 10, General MIDI percussion.</summary>
    Channel10,

    /// <summary>A word in the track name ("Bass", "Lead Gtr").</summary>
    TrackName,

    /// <summary>The General MIDI program the part starts with.</summary>
    Program,

    /// <summary>Nothing to go on: <see cref="Roles.Other"/>.</summary>
    None,
}

/// <summary>
/// Tags song parts with roles (ADR 0012): saved mapping, then channel 10, then track name, then
/// General MIDI program. Names win over programs because a person typed them; many files leave
/// every track on program 0 (piano).
/// </summary>
public static partial class RoleDetector
{
    // Order matters: "bassoon" is winds, not bass; "drum" before anything that a kit track might also say.
    private static readonly (Regex Pattern, string Role)[] NameRules =
    [
        (Words("drums?|kit|perc(ussion)?|snare|kick|hi-?hats?|cymbals?|toms"), Roles.Drums),
        (Words("bassoons?|flutes?|clarinets?|oboes?|sax(ophone)?s?|piccolo|recorder|winds?|woodwinds?|reeds?"), Roles.Winds),
        (Words("bass(line)?|basses"), Roles.Bass),
        (Words("guitars?|gtrs?|gt|strat|banjo|ukulele|uke"), Roles.Guitar),
        (Words("piano|keys|keyboards?|organ|rhodes|e-?piano|epiano|clav(inet)?|synth|harpsichord|pad"), Roles.Keys),
        (Words("strings?|violins?|violas?|cellos?|fiddle|orchestra|ensemble|harp"), Roles.Strings),
        (Words("brass|trumpets?|trombones?|horns?|tubas?|cornet"), Roles.Brass),
        (Words("vocals?|vox|voice|voices|choir|singer|sing"), Roles.Vocals),
        (Words("melody|lead|tune|theme|xylo(phone)?|marimba|vibes|vibraphone|glock(enspiel)?|bells|mallets?"), Roles.Melody),
    ];

    /// <summary>The role for <paramref name="part"/> and why.</summary>
    /// <param name="part">The song part.</param>
    /// <param name="program">The General MIDI program (0-127) the part starts with, if the file says.</param>
    /// <param name="mapping">The song's saved mapping, if any.</param>
    public static (string Role, RoleSource Source) Detect(SongPart part, int? program, RoleMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (mapping?.Find(part) is { } mapped)
        {
            return (mapped, RoleSource.Mapping);
        }

        if (part.IsPercussion)
        {
            return (Roles.Drums, RoleSource.Channel10);
        }

        if (FromName(part.Name) is { } named)
        {
            return (named, RoleSource.TrackName);
        }

        return program is { } p ? (FromProgram(p), RoleSource.Program) : (Roles.Other, RoleSource.None);
    }

    /// <summary>The role a track name suggests, or null ("Track 3" suggests nothing).</summary>
    public static string? FromName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : NameRules.FirstOrDefault(r => r.Pattern.IsMatch(name)).Role;

    /// <summary>The role of a General MIDI program (0-127), by its instrument family.</summary>
    public static string FromProgram(int program) => program switch
    {
        >= 0 and <= 7 => Roles.Keys,        // pianos
        >= 8 and <= 15 => Roles.Melody,     // chromatic percussion: celesta, glockenspiel, xylophone…
        >= 16 and <= 23 => Roles.Keys,      // organs, accordion, harmonica
        >= 24 and <= 31 => Roles.Guitar,
        >= 32 and <= 39 => Roles.Bass,
        >= 40 and <= 51 => Roles.Strings,   // strings, ensembles
        >= 52 and <= 54 => Roles.Vocals,    // choir aahs, voice oohs, synth voice
        55 => Roles.Strings,                // orchestra hit
        >= 56 and <= 63 => Roles.Brass,
        >= 64 and <= 79 => Roles.Winds,     // reeds, pipes
        >= 80 and <= 87 => Roles.Melody,    // synth leads
        >= 88 and <= 95 => Roles.Keys,      // synth pads
        _ => Roles.Other,                   // effects, ethnic, percussive, sound effects
    };

    private static Regex Words(string alternatives) =>
        new($@"(?<![a-z0-9])({alternatives})(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// A song's saved role choices: <c>song.roles.json</c> next to <c>song.mid</c>, so the owner tags
/// a badly named file once. Keys pick parts by track name (any case), <c>#TRACK</c> (1-based), or
/// <c>#TRACK:CHANNEL</c> (both 1-based); the most specific key wins.
/// </summary>
/// <remarks>Format: <c>{ "schema": "metrodom.song-roles/0", "parts": { "Lead": "melody", "#3": "guitar" } }</c>.</remarks>
public sealed class RoleMapping
{
    /// <summary>The schema this code reads and writes.</summary>
    public const string Schema = "metrodom.song-roles/0";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Dictionary<string, string> _parts;

    public RoleMapping(IReadOnlyDictionary<string, string>? parts = null)
    {
        _parts = new Dictionary<string, string>(parts ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Key → role, as saved.</summary>
    public IReadOnlyDictionary<string, string> Parts => _parts;

    /// <summary>Where the mapping for <paramref name="songPath"/> lives: <c>song.roles.json</c> beside <c>song.mid</c>.</summary>
    public static string PathFor(string songPath) => Path.ChangeExtension(songPath, ".roles.json");

    /// <summary>The key that picks exactly one track and channel: <c>#3:10</c>.</summary>
    public static string KeyFor(SongPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return string.Create(CultureInfo.InvariantCulture, $"#{part.Track + 1}:{part.Channel + 1}");
    }

    /// <summary>The saved role for <paramref name="part"/>, or null.</summary>
    public string? Find(SongPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return _parts.GetValueOrDefault(KeyFor(part))
            ?? _parts.GetValueOrDefault(string.Create(CultureInfo.InvariantCulture, $"#{part.Track + 1}"))
            ?? _parts.GetValueOrDefault(part.Name);
    }

    /// <summary>Sets (or with a null role, removes) one key.</summary>
    public RoleMapping With(string key, string? role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var copy = new RoleMapping(_parts);
        if (role is null)
        {
            copy._parts.Remove(key.Trim());
        }
        else
        {
            copy._parts[key.Trim()] = role;
        }

        return copy;
    }

    /// <summary>Reads a mapping, or returns false with a reason.</summary>
    public static bool TryParse(string json, out RoleMapping mapping, out string? error)
    {
        mapping = new RoleMapping();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "not a JSON object.";
                return false;
            }

            if (root["schema"]?.GetValue<string>() != Schema)
            {
                error = $"\"schema\" must be {Schema}.";
                return false;
            }

            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in root["parts"] as JsonObject ?? [])
            {
                if (!Roles.TryNormalize(value?.GetValue<string>(), out var role) || string.IsNullOrWhiteSpace(key))
                {
                    error = $"part '{key}' needs a one-word role.";
                    return false;
                }

                parts[key.Trim()] = role;
            }

            mapping = new RoleMapping(parts);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = $"not a valid roles file: {ex.Message}";
            return false;
        }
    }

    /// <summary>The mapping as JSON, keys sorted so diffs stay small.</summary>
    public string ToJson()
    {
        var parts = new JsonObject();
        foreach (var (key, role) in _parts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            parts[key] = role;
        }

        return new JsonObject { ["schema"] = Schema, ["parts"] = parts }.ToJsonString(WriteOptions) + "\n";
    }
}
