using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetroDom.Core.Calibration;

/// <summary>Latencies measured for one output on this rig: per note, plus one for notes without their own.</summary>
public sealed record MeasuredLatency(string Port, DateTimeOffset MeasuredAt, TimeSpan Latency, IReadOnlyDictionary<int, TimeSpan> NoteLatency)
{
    /// <summary>The measured latencies in place of <paramref name="profile"/>'s (everything else kept).</summary>
    public InstrumentProfile ApplyTo(InstrumentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with { Latency = Latency, NoteLatency = NoteLatency };
    }

    /// <summary>What a run measured, or null when it heard nothing.</summary>
    public static MeasuredLatency? From(string port, CalibrationResult result, InstrumentProfile profile, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.HeardNone)
        {
            return null;
        }

        var measured = result.ApplyTo(profile);
        return new MeasuredLatency(port, at, measured.Latency, measured.NoteLatency);
    }
}

/// <summary>
/// Measured latencies by output port name (<c>metrodom.calibrations/0</c>). A measurement belongs
/// to the instrument on this rig (its mic or trigger, its cables), so it lives on the Conductor,
/// not in the instrument's own profile. Pure data: reading and writing the file is the
/// Conductor's job.
/// </summary>
public sealed class CalibrationBook
{
    public const string Schema = "metrodom.calibrations/0";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, MeasuredLatency> _byPort = new(StringComparer.Ordinal);

    public IReadOnlyCollection<MeasuredLatency> All => _byPort.Values;

    public MeasuredLatency? Find(string port) => _byPort.GetValueOrDefault(port);

    public void Set(MeasuredLatency calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        _byPort[calibration.Port] = calibration;
    }

    public bool Remove(string port) => _byPort.Remove(port);

    public static bool TryParse(string json, out CalibrationBook book, out string? error)
    {
        book = new CalibrationBook();
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

            foreach (var item in root["calibrations"]?.AsArray() ?? [])
            {
                if (item is not JsonObject entry
                    || entry["port"]?.GetValue<string>() is not { Length: > 0 } port
                    || entry["latencyMs"]?.GetValue<double>() is not { } latency || latency < 0
                    || !DateTimeOffset.TryParse(entry["measuredAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                {
                    error = "each calibration needs \"port\", \"measuredAt\" and a \"latencyMs\" of 0 or more.";
                    return false;
                }

                var notes = new Dictionary<int, TimeSpan>();
                foreach (var n in entry["notes"]?.AsArray() ?? [])
                {
                    if (n?["note"]?.GetValue<int>() is not { } note || note is < 0 or > 127
                        || n["latencyMs"]?.GetValue<double>() is not { } noteLatency || noteLatency < 0)
                    {
                        error = $"{port}: each note needs \"note\" (0-127) and \"latencyMs\" of 0 or more.";
                        return false;
                    }

                    notes[note] = TimeSpan.FromMilliseconds(noteLatency);
                }

                book.Set(new MeasuredLatency(port, at, TimeSpan.FromMilliseconds(latency), notes));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = ex.Message;
            return false;
        }

        error = null;
        return true;
    }

    public string ToJson()
    {
        var list = new JsonArray();
        foreach (var c in _byPort.Values.OrderBy(c => c.Port, StringComparer.Ordinal))
        {
            var notes = new JsonArray();
            foreach (var (note, latency) in c.NoteLatency.OrderBy(kv => kv.Key))
            {
                notes.Add(new JsonObject { ["note"] = note, ["latencyMs"] = Math.Round(latency.TotalMilliseconds, 2) });
            }

            list.Add(new JsonObject
            {
                ["port"] = c.Port,
                ["measuredAt"] = c.MeasuredAt.ToString("O", CultureInfo.InvariantCulture),
                ["latencyMs"] = Math.Round(c.Latency.TotalMilliseconds, 2),
                ["notes"] = notes,
            });
        }

        return new JsonObject { ["schema"] = Schema, ["calibrations"] = list }.ToJsonString(WriteOptions);
    }
}
