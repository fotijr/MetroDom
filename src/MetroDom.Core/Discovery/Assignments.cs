using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetroDom.Core.Discovery;

/// <summary>
/// What the owner decided one device plays (ADR 0007, step 4): its channels (0-15) and, when the
/// owner set them, latency and notes. <see cref="Key"/> is <see cref="AssignmentBook.KeyFor"/>.
/// </summary>
public sealed record Assignment(string Key, string Port, IReadOnlyList<int> Channels, TimeSpan? Latency = null, IReadOnlySet<int>? Notes = null);

/// <summary>
/// The remembered assignments (<c>metrodom.assignments/0</c> JSON), so a device plugged in again
/// gets the role the owner gave it last time. A device is keyed by its Identity Reply plus port
/// name, or by port name alone when it doesn't answer. Pure data: reading and writing the file is
/// the Conductor's job.
/// </summary>
public sealed class AssignmentBook
{
    /// <summary>The schema this code reads and writes.</summary>
    public const string Schema = "metrodom.assignments/0";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly List<Assignment> _assignments = [];

    /// <summary>Every assignment, oldest first.</summary>
    public IReadOnlyList<Assignment> All => _assignments;

    /// <summary>
    /// The stable key for a device: <c>"7D:224D:0001 MetroDom xylo"</c> (manufacturer, family and
    /// model in hex, then the port), or just the port name for a device that didn't answer. The
    /// firmware version is left out, so updating firmware keeps the assignment.
    /// </summary>
    public static string KeyFor(string port, DeviceIdentity? identity) =>
        identity is null
            ? port
            : string.Create(CultureInfo.InvariantCulture, $"{identity.ManufacturerId}:{identity.Family:X4}:{identity.Model:X4} {port}");

    /// <summary>
    /// The assignment for this device: by its key, else one made for the same port while the
    /// device didn't answer (a port-only key), so an assignment survives a firmware that learns
    /// to identify itself.
    /// </summary>
    public Assignment? Find(string port, DeviceIdentity? identity)
    {
        var key = KeyFor(port, identity);
        return _assignments.FirstOrDefault(a => a.Key == key)
            ?? _assignments.FirstOrDefault(a => a.Key == port && a.Port == port);
    }

    /// <summary>Adds <paramref name="assignment"/>, replacing any with the same key.</summary>
    public void Set(Assignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var index = _assignments.FindIndex(a => a.Key == assignment.Key);
        if (index >= 0)
        {
            _assignments[index] = assignment;
        }
        else
        {
            _assignments.Add(assignment);
        }
    }

    /// <summary>
    /// Removes the assignments whose key or port is <paramref name="what"/>, or else whose port
    /// contains it (ignoring case). Returns what was removed.
    /// </summary>
    public IReadOnlyList<Assignment> Remove(string what)
    {
        ArgumentNullException.ThrowIfNull(what);
        var exact = _assignments.Where(a => a.Key == what || a.Port == what).ToList();
        var removed = exact.Count > 0 ? exact : _assignments.Where(a => a.Port.Contains(what, StringComparison.OrdinalIgnoreCase)).ToList();
        _assignments.RemoveAll(removed.Contains);
        return removed;
    }

    /// <summary>Parses the file, or returns false with a reason a person can act on.</summary>
    public static bool TryParse(string json, out AssignmentBook book, out string? error)
    {
        book = new AssignmentBook();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "not a JSON object.";
                return false;
            }

            if (root["schema"]?.GetValue<string>() is not Schema)
            {
                error = $"not {Schema}.";
                return false;
            }

            foreach (var item in root["assignments"] as JsonArray ?? [])
            {
                if (!TryRead(item, out var assignment, out error))
                {
                    return false;
                }

                book.Set(assignment);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = $"not valid: {ex.Message}";
            return false;
        }
    }

    /// <summary>Writes the book as indented JSON.</summary>
    public string ToJson()
    {
        var list = new JsonArray();
        foreach (var a in _assignments)
        {
            var item = new JsonObject
            {
                ["key"] = a.Key,
                ["port"] = a.Port,
                ["channels"] = new JsonArray([.. a.Channels.Select(c => (JsonNode)(c + 1))]),
            };
            if (a.Latency is { } latency)
            {
                item["latencyMs"] = latency.TotalMilliseconds;
            }

            if (a.Notes is { } notes)
            {
                item["notes"] = new JsonArray([.. notes.Order().Select(n => (JsonNode)n)]);
            }

            list.Add(item);
        }

        return new JsonObject { ["schema"] = Schema, ["assignments"] = list }.ToJsonString(WriteOptions);
    }

    private static bool TryRead(JsonNode? item, out Assignment assignment, out string? error)
    {
        assignment = null!;
        var key = item?["key"]?.GetValue<string>();
        var port = item?["port"]?.GetValue<string>();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(port))
        {
            error = "every assignment needs a \"key\" and a \"port\".";
            return false;
        }

        var channels = (item!["channels"] as JsonArray ?? []).Select(c => c!.GetValue<int>() - 1).ToArray();
        if (channels.Length == 0 || channels.Any(c => c is < 0 or > 15))
        {
            error = $"{port}: \"channels\" must list 1-16.";
            return false;
        }

        HashSet<int>? notes = null;
        if (item["notes"] is JsonArray noteArray)
        {
            notes = [.. noteArray.Select(n => n!.GetValue<int>())];
            if (notes.Any(n => n is < 0 or > 127))
            {
                error = $"{port}: notes must be 0-127.";
                return false;
            }
        }

        TimeSpan? latency = item["latencyMs"] is { } ms ? TimeSpan.FromMilliseconds(ms.GetValue<double>()) : null;
        if (latency < TimeSpan.Zero)
        {
            error = $"{port}: latency can't be negative.";
            return false;
        }

        assignment = new Assignment(key, port, channels, latency, notes);
        error = null;
        return true;
    }
}
