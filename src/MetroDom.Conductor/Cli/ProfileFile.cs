using MetroDom.Core;

namespace MetroDom.Conductor.Cli;

/// <summary>
/// An instrument's <c>profile.json</c> named on the command line where a latency would go
/// (<c>-i xylo@Instruments/xylophone/profile.json</c>, <c>-o "MetroDom xylo@profile.json"</c>):
/// the instrument's notes, latencies, minimum interval, polyphony and safety limits, as the
/// instrument itself would send them (docs/references/instrument-profile.md).
/// </summary>
internal static class ProfileFile
{
    /// <summary>Whether <paramref name="text"/> names a profile file rather than a latency.</summary>
    public static bool Names(string text) => text.Trim().EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public static bool TryLoad(string path, out InstrumentProfile profile, out string? error)
    {
        profile = null!;
        path = path.Trim();
        if (!File.Exists(path))
        {
            error = $"profile not found: {path}";
            return false;
        }

        try
        {
            if (!InstrumentProfileJson.TryParse(File.ReadAllText(path), out profile, out var parseError))
            {
                error = $"{path}: {parseError}";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"can't read {path}: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }
}
