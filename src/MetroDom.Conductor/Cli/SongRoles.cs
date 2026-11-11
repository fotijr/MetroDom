using MetroDom.Core.Arranging;

namespace MetroDom.Conductor.Cli;

/// <summary>Reads and writes a song's saved roles (<c>song.roles.json</c> next to <c>song.mid</c>).</summary>
internal static class SongRoles
{
    /// <summary>The saved mapping for <paramref name="songPath"/>, or null if there is none (or no file).</summary>
    public static RoleMapping? Load(string? songPath)
    {
        if (songPath is null || !File.Exists(RoleMapping.PathFor(songPath)))
        {
            return null;
        }

        var path = RoleMapping.PathFor(songPath);
        return RoleMapping.TryParse(File.ReadAllText(path), out var mapping, out var error)
            ? mapping
            : throw new UsageException($"{Path.GetFileName(path)}: {error}");
    }

    public static void Save(string songPath, RoleMapping mapping) =>
        File.WriteAllText(RoleMapping.PathFor(songPath), mapping.ToJson());
}
