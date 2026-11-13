using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetroDom.Conductor.Api;
using MetroDom.Core.Policy;

namespace MetroDom.Conductor.Service;

/// <summary>
/// What <c>serve</c> keeps across restarts (<c>metrodom.service-state/0</c>, plan 0004 S3): the
/// owner token, so phones paired before a restart still work; handed-out tokens as hashes, never
/// their secrets; and the songs still to play, by library id. One JSON file, written whole to a
/// temporary file and moved into place, readable by its owner only (on Linux and macOS).
/// </summary>
internal sealed class ServiceState
{
    public const string Schema = "metrodom.service-state/0";

    public const string FileName = "state.json";

    private readonly Lock _gate = new();

    private ServiceState(string path)
    {
        Path = path;
    }

    /// <summary>Where it's saved.</summary>
    public string Path { get; }

    /// <summary>The owner token: saved, or new on first run.</summary>
    public string OwnerToken { get; private set; } = "";

    /// <summary>Handed-out tokens as saved.</summary>
    public IReadOnlyList<(TokenHolder Holder, byte[] Hash)> Tokens { get; private set; } = [];

    /// <summary>Library ids of the songs that were still to play.</summary>
    public IReadOnlyList<string> Queue { get; private set; } = [];

    /// <summary>
    /// The default folder: <c>$XDG_STATE_HOME/metrodom</c>, else <c>~/.local/state/metrodom</c>
    /// on Linux and macOS, <c>%LOCALAPPDATA%\MetroDom</c> on Windows.
    /// </summary>
    public static string DefaultFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroDom");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return string.IsNullOrEmpty(xdg)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "metrodom")
            : System.IO.Path.Combine(xdg, "metrodom");
    }

    /// <summary>
    /// Reads the state in <paramref name="folder"/>, or starts fresh (new owner token) when there's
    /// none. A file that can't be read is an error, not silently replaced: it holds the owner token.
    /// </summary>
    public static bool TryLoad(string folder, out ServiceState state, out string? error)
    {
        state = new ServiceState(System.IO.Path.Combine(System.IO.Path.GetFullPath(folder), FileName));
        error = null;
        if (!File.Exists(state.Path))
        {
            state.OwnerToken = ControlApi.NewToken();
            return true;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(state.Path)) is not JsonObject root || (string?)root["schema"] != Schema)
            {
                error = $"{state.Path} is not a {Schema} file.";
                return false;
            }

            if ((string?)root["ownerToken"] is not { Length: >= 16 } owner)
            {
                error = $"{state.Path} has no owner token; delete the file to start fresh (every phone pairs again).";
                return false;
            }

            state.OwnerToken = owner;
            state.Tokens = [.. (root["tokens"] as JsonArray ?? []).OfType<JsonObject>().Select(ReadToken).OfType<(TokenHolder, byte[])>()];
            state.Queue = [.. (root["queue"] as JsonArray ?? []).Select(n => (string?)n).OfType<string>()];
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            error = $"can't read {state.Path}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Saves the owner token, <paramref name="tokens"/> and <paramref name="queue"/> (library ids).</summary>
    public void Save(IReadOnlyList<(TokenHolder Holder, byte[] Hash)> tokens, IReadOnlyList<string> queue)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(queue);
        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["ownerToken"] = OwnerToken,
            ["tokens"] = new JsonArray([.. tokens.Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Holder.Id,
                ["role"] = RolePermissions.NameOf(t.Holder.Role),
                ["label"] = t.Holder.Label,
                ["issuedAt"] = t.Holder.Issued?.ToString("O", CultureInfo.InvariantCulture),
                ["expiresAt"] = t.Holder.Expires?.ToString("O", CultureInfo.InvariantCulture),
                ["join"] = t.Holder.Door ? true : null,
                ["via"] = t.Holder.Via,
                ["sha256"] = Convert.ToHexStringLower(t.Hash),
            })]),
            ["queue"] = new JsonArray([.. queue.Select(id => (JsonNode)id)]),
        };

        lock (_gate)
        {
            var folder = System.IO.Path.GetDirectoryName(Path)!;
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(folder);
            }

            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, Path, overwrite: true);
            Tokens = tokens;
            Queue = queue;
        }
    }

    private static (TokenHolder, byte[])? ReadToken(JsonObject node)
    {
        if ((string?)node["id"] is not { Length: > 0 } id
            || (string?)node["role"] switch { "operator" => Role.Operator, "guest" => Role.Guest, _ => (Role?)null } is not { } role
            || (string?)node["sha256"] is not { Length: 64 } hash
            || !DateTimeOffset.TryParse((string?)node["expiresAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires))
        {
            return null;
        }

        DateTimeOffset? issued = DateTimeOffset.TryParse((string?)node["issuedAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
        var door = role == Role.Guest && (bool?)node["join"] == true;
        return (new TokenHolder(id, role, (string?)node["label"], issued, expires, door, (string?)node["via"]), Convert.FromHexString(hash));
    }
}
