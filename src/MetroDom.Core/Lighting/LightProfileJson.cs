using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetroDom.Core.Lighting;

/// <summary>
/// Reads the <a href="../../../docs/references/light-profile.md">light profile</a> JSON
/// (<c>metrodom.light-profile/0</c>).
/// </summary>
public static class LightProfileJson
{
    public const string Schema = "metrodom.light-profile/0";

    /// <summary>Parses a profile, or returns false with a reason a person can act on.</summary>
    public static bool TryParse(string json, out LightProfile profile, out string? error)
    {
        profile = null!;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "not a JSON object.";
                return false;
            }

            return TryRead(root, out profile, out error);
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            error = $"a field has the wrong type: {ex.Message}";
            return false;
        }
    }

    private static bool TryRead(JsonObject root, out LightProfile profile, out string? error)
    {
        profile = null!;
        var schema = root["schema"]?.GetValue<string>();
        if (schema != Schema)
        {
            error = schema is null ? "missing \"schema\"." : $"schema '{schema}' is not {Schema}.";
            return false;
        }

        var name = root["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "missing \"name\".";
            return false;
        }

        if (!TryFixtures(root["fixtures"] as JsonArray, out var fixtures, out error))
        {
            return false;
        }

        var ids = fixtures.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var scenes = new List<LightScene>();
        foreach (var node in root["scenes"] as JsonArray ?? [])
        {
            var note = node?["note"]?.GetValue<int>();
            if (note is not (>= 0 and <= 127))
            {
                error = "every scene needs a \"note\" from 0 to 127.";
                return false;
            }

            if (scenes.Any(s => s.Note == note))
            {
                error = $"two scenes use note {note}.";
                return false;
            }

            if (!TryLook(node!["set"], ids, $"scene {note}", out var look, out error))
            {
                return false;
            }

            scenes.Add(new LightScene(note.Value, node["name"]?.GetValue<string>() ?? $"Scene {note}", look));
        }

        var safe = LightLook.DefaultSafe;
        if (root["safeLook"] is { } safeNode && !TryLook(safeNode, ids, "safeLook", out safe, out error))
        {
            return false;
        }

        if (!TryReactive(root["reactive"], ids, out var reactive, out error))
        {
            return false;
        }

        var channel = root["midi"]?["channel"]?.GetValue<int>() ?? LightProfile.DefaultChannel + 1;
        var cues = root["midi"]?["cues"]?.GetValue<string>() ?? "hold";
        var masterCc = root["controls"]?["masterCc"]?.GetValue<int>() ?? LightProfile.DefaultMasterCc;
        var latency = root["latencyMs"]?.GetValue<double>() ?? 0;
        var destination = root["output"]?["destination"]?.GetValue<string>();
        var priority = root["output"]?["priority"]?.GetValue<int>() ?? LightOutput.DefaultPriority;
        var protocol = root["output"]?["protocol"]?.GetValue<string>() ?? "sacn";
        error = channel is < 1 or > 16 ? "\"midi.channel\" must be 1-16."
            : cues is not ("hold" or "latch") ? $"\"midi.cues\" '{cues}' must be hold or latch."
            : masterCc is < 0 or > 119 ? "\"controls.masterCc\" must be 0-119."
            : latency is < 0 or > 10_000 ? "\"latencyMs\" must be 0-10000."
            : priority is < 0 or > 200 ? "\"output.priority\" must be 0-200."
            : protocol != "sacn" ? $"\"output.protocol\" '{protocol}' isn't supported (only sacn)."
            : destination is { Length: 0 } ? "\"output.destination\" is empty (leave it out for multicast)."
            : null;
        if (error is not null)
        {
            return false;
        }

        profile = new LightProfile(name, fixtures)
        {
            Channel = channel - 1,
            Cues = cues == "latch" ? CueMode.Latch : CueMode.Hold,
            MasterCc = masterCc,
            Latency = TimeSpan.FromMilliseconds(latency),
            Scenes = scenes,
            SafeLook = safe,
            Output = new LightOutput(destination, priority),
            Reactive = reactive,
        };
        return true;
    }

    private static bool TryFixtures(JsonArray? array, out List<Fixture> fixtures, out string? error)
    {
        fixtures = [];
        if (array is not { Count: > 0 })
        {
            error = "\"fixtures\" must list at least one fixture.";
            return false;
        }

        foreach (var node in array)
        {
            var id = node?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || id == LightLook.AllFixtures)
            {
                error = "every fixture needs an \"id\" (and \"*\" is reserved).";
                return false;
            }

            if (fixtures.Any(f => f.Id == id))
            {
                error = $"fixture '{id}' is listed twice.";
                return false;
            }

            var universe = node!["universe"]?.GetValue<int>() ?? 1;
            var address = node["address"]?.GetValue<int>() ?? 0;
            var count = node["count"]?.GetValue<int>() ?? 1;
            var channels = (node["channels"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
            error = universe is < 1 or > 63_999 ? $"{id}: \"universe\" must be 1-63999."
                : address is < 1 or > LightRenderer.SlotsPerUniverse ? $"{id}: \"address\" must be 1-512."
                : channels.Count == 0 || channels.Any(string.IsNullOrWhiteSpace) ? $"{id}: \"channels\" must name each channel (e.g. [\"dimmer\", \"red\", \"green\", \"blue\"])."
                : count is < 1 ? $"{id}: \"count\" must be at least 1."
                : null;
            if (error is not null)
            {
                return false;
            }

            var fixture = new Fixture(id, universe, address, channels!, count);
            if (address - 1 + fixture.Footprint > LightRenderer.SlotsPerUniverse)
            {
                error = $"{id}: {fixture.Footprint} channels from address {address} run past 512.";
                return false;
            }

            if (fixtures.FirstOrDefault(f => f.Universe == universe && Overlaps(f, fixture)) is { } other)
            {
                error = $"{id} and {other.Id} overlap in universe {universe}.";
                return false;
            }

            fixtures.Add(fixture);
        }

        error = null;
        return true;
    }

    private static bool TryReactive(JsonNode? node, HashSet<string> ids, out ReactiveSettings reactive, out string? error)
    {
        reactive = new ReactiveSettings();
        error = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            error = "\"reactive\" must be an object.";
            return false;
        }

        var baseLevel = obj["baseLevel"]?.GetValue<int>() ?? reactive.BaseLevel;
        var decay = obj["decayMs"]?.GetValue<double>() ?? reactive.Decay.TotalMilliseconds;
        if (baseLevel is < 0 or > 255 || decay is < 0 or > 10_000)
        {
            error = "\"reactive\": baseLevel must be 0-255 and decayMs 0-10000.";
            return false;
        }

        var colors = reactive.Colors.ToArray();
        foreach (var (key, value) in obj["colors"] as JsonObject ?? [])
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var channel) || channel is < 1 or > 16
                || !Rgb.TryParse(value?.GetValue<string>(), out var color))
            {
                error = $"\"reactive.colors\": '{key}' needs a channel 1-16 and a color like \"#ffb000\".";
                return false;
            }

            colors[channel - 1] = color;
        }

        var follow = new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal);
        foreach (var (fixture, value) in obj["follow"] as JsonObject ?? [])
        {
            var channels = (value as JsonArray ?? []).Select(c => c!.GetValue<int>()).ToList();
            if (!ids.Contains(fixture) || channels.Count == 0 || channels.Any(c => c is < 1 or > 16))
            {
                error = $"\"reactive.follow\": '{fixture}' must be a fixture with a list of channels 1-16.";
                return false;
            }

            follow[fixture] = channels.Select(c => c - 1).ToHashSet();
        }

        reactive = new ReactiveSettings
        {
            BaseLevel = (byte)baseLevel,
            Decay = TimeSpan.FromMilliseconds(decay),
            Colors = colors,
            Follow = follow,
        };
        return true;
    }

    private static bool Overlaps(Fixture a, Fixture b) =>
        a.Address < b.Address + b.Footprint && b.Address < a.Address + a.Footprint;

    private static bool TryLook(JsonNode? node, HashSet<string> ids, string where, out LightLook look, out string? error)
    {
        look = LightLook.Blackout;
        if (node is not JsonObject obj)
        {
            error = $"{where}: \"set\" must be an object of fixture → {{ role: value }}.";
            return false;
        }

        var values = new Dictionary<string, IReadOnlyDictionary<string, byte>>(StringComparer.Ordinal);
        foreach (var (fixture, rolesNode) in obj)
        {
            if (fixture != LightLook.AllFixtures && !ids.Contains(fixture))
            {
                error = $"{where}: no fixture '{fixture}'.";
                return false;
            }

            if (rolesNode is not JsonObject roles)
            {
                error = $"{where}: '{fixture}' must map roles to values.";
                return false;
            }

            var set = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            foreach (var (role, valueNode) in roles)
            {
                var value = valueNode?.GetValue<int>();
                if (value is not (>= 0 and <= 255))
                {
                    error = $"{where}: {fixture}.{role} must be 0-255.";
                    return false;
                }

                set[role] = (byte)value.Value;
            }

            values[fixture] = set;
        }

        look = new LightLook(values);
        error = null;
        return true;
    }
}
