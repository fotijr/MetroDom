using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetroDom.Core;

/// <summary>
/// Reads and writes the <a href="../../docs/references/instrument-profile.md">instrument profile</a>
/// JSON (<c>metrodom.instrument-profile/0</c>). Only the fields the Conductor uses today become
/// <see cref="InstrumentProfile"/> properties (of <c>midi</c>, only the channel); the rest (identity, polyphony, safety) are accepted
/// and ignored until something needs them.
/// </summary>
public static class InstrumentProfileJson
{
    /// <summary>The schema this code reads and writes.</summary>
    public const string Schema = "metrodom.instrument-profile/0";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // Escape everything outside ASCII, so the text fits 7-bit SysEx data bytes unchanged.
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>Parses a profile, or returns false with a reason a person can act on.</summary>
    public static bool TryParse(string json, out InstrumentProfile profile, out string? error)
    {
        profile = null!;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON: {ex.Message}";
            return false;
        }

        if (root is not JsonObject obj)
        {
            error = "not a JSON object.";
            return false;
        }

        try
        {
            return TryRead(obj, out profile, out error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            error = $"a field has the wrong type: {ex.Message}";
            return false;
        }
    }

    private static bool TryRead(JsonObject obj, out InstrumentProfile profile, out string? error)
    {
        profile = null!;
        var schema = obj["schema"]?.GetValue<string>();
        if (schema != Schema)
        {
            error = schema is null ? "missing \"schema\"." : $"schema '{schema}' is not {Schema}.";
            return false;
        }

        var name = obj["name"]?.GetValue<string>() ?? obj["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "missing \"name\".";
            return false;
        }

        var latencies = new Dictionary<int, TimeSpan>();
        var minInterval = Milliseconds(obj["minIntervalMs"]) ?? TimeSpan.Zero;
        HashSet<int>? notes = null;
        if (obj["notes"] is JsonArray array)
        {
            notes = [];
            foreach (var item in array)
            {
                var note = item?["note"]?.GetValue<int>();
                if (note is not (>= 0 and <= 127))
                {
                    error = "every entry in \"notes\" needs a \"note\" from 0 to 127.";
                    return false;
                }

                if (!notes.Add(note.Value))
                {
                    error = $"note {note} is listed twice.";
                    return false;
                }

                if (Milliseconds(item!["latencyMs"]) is { } latency)
                {
                    latencies[note.Value] = latency;
                }

                // One interval for the whole instrument: the slowest note's, so nothing is refused.
                if (Milliseconds(item["minIntervalMs"]) is { } interval && interval > minInterval)
                {
                    minInterval = interval;
                }
            }
        }

        // Notes without their own latency fall back to the top-level one, else the slowest note.
        var defaultLatency = Milliseconds(obj["latencyMs"]) ?? (latencies.Count > 0 ? latencies.Values.Max() : TimeSpan.Zero);
        if (defaultLatency < TimeSpan.Zero || latencies.Values.Any(l => l < TimeSpan.Zero) || minInterval < TimeSpan.Zero)
        {
            error = "latencies and intervals can't be negative.";
            return false;
        }

        int? channel = null;
        if (obj["midi"]?["channel"] is { } channelNode)
        {
            channel = channelNode.GetValue<int>();
            if (channel is not (>= 1 and <= 16))
            {
                error = "\"midi.channel\" must be 1-16.";
                return false;
            }

            channel--;
        }

        profile = new InstrumentProfile(name, defaultLatency)
        {
            Channel = channel,
            Notes = notes,
            NoteLatency = latencies,
            MinInterval = minInterval,
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Writes <paramref name="profile"/> as compact, ASCII-only JSON. <paramref name="id"/> defaults
    /// to the name. An instrument that plays any note gets no <c>notes</c> array.
    /// </summary>
    public static string Write(InstrumentProfile profile, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var obj = new JsonObject
        {
            ["schema"] = Schema,
            ["id"] = id ?? profile.Name,
            ["name"] = profile.Name,
            ["latencyMs"] = profile.Latency.TotalMilliseconds,
        };
        if (profile.Channel is { } channel)
        {
            obj["midi"] = new JsonObject { ["channel"] = channel + 1 };
        }

        if (profile.MinInterval > TimeSpan.Zero)
        {
            obj["minIntervalMs"] = profile.MinInterval.TotalMilliseconds;
        }

        if (profile.Notes is not null)
        {
            var notes = new JsonArray();
            foreach (var note in profile.Notes.Order())
            {
                var entry = new JsonObject { ["note"] = note };
                if (profile.NoteLatency.TryGetValue(note, out var latency))
                {
                    entry["latencyMs"] = latency.TotalMilliseconds;
                }

                notes.Add(entry);
            }

            obj["notes"] = notes;
        }

        return obj.ToJsonString(WriteOptions);
    }

    private static TimeSpan? Milliseconds(JsonNode? node) =>
        node is null ? null : TimeSpan.FromMilliseconds(node.GetValue<double>());
}
