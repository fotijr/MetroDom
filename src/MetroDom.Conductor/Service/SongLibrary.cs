using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Conductor.Service;

/// <summary>A song the service can queue.</summary>
/// <param name="Id">Its path under the library folder, with <c>/</c> separators (stable across restarts).</param>
/// <param name="Title">The file name without its extension.</param>
/// <param name="Path">Where it is on disk. Never sent to clients.</param>
/// <param name="Duration">Length at file tempo.</param>
/// <param name="Bpm">The file's starting tempo.</param>
internal sealed record LibrarySong(string Id, string Title, string Path, TimeSpan Duration, double Bpm);

/// <summary>
/// The <c>serve</c> library: every Standard MIDI File under one folder (plan 0004 S3). Rescans
/// when asked, reading only files that are new or changed. Clients only ever name songs by
/// <see cref="LibrarySong.Id"/>, and only ids from the last scan resolve, so a request can't
/// reach a file outside the folder. Thread-safe.
/// </summary>
internal sealed class SongLibrary
{
    /// <summary>More than any band's book; a cap so a wrong folder (a whole disk) doesn't stall the box.</summary>
    public const int MaxSongs = 5000;

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,

        // Links could lead out of the folder; hidden files are editors' and OS droppings.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime Written, long Length, LibrarySong? Song)> _seen = new(StringComparer.Ordinal);
    private IReadOnlyList<LibrarySong> _songs = [];
    private Dictionary<string, LibrarySong> _byId = new(StringComparer.Ordinal);

    public SongLibrary(string root)
    {
        Root = System.IO.Path.GetFullPath(root);
        Rescan();
    }

    /// <summary>The library folder.</summary>
    public string Root { get; }

    /// <summary>Files that aren't readable MIDI after the last scan (shown once to the owner).</summary>
    public IReadOnlyList<string> Unreadable { get; private set; } = [];

    /// <summary>Songs as of the last scan, by title.</summary>
    public IReadOnlyList<LibrarySong> Songs
    {
        get
        {
            lock (_gate)
            {
                return _songs;
            }
        }
    }

    /// <summary>The song with this id as of the last scan, or null.</summary>
    public LibrarySong? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    /// <summary>How stale <see cref="Fresh"/> lets the list get: a phone opening the library doesn't walk the disk each time.</summary>
    public static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(5);

    private long _scannedAt;

    /// <summary>The songs, rescanning first if the last scan is older than <see cref="RescanEvery"/>.</summary>
    public IReadOnlyList<LibrarySong> Fresh()
    {
        lock (_gate)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_scannedAt) < RescanEvery)
            {
                return _songs;
            }
        }

        return Rescan();
    }

    /// <summary>Looks at the folder again. Returns the songs.</summary>
    public IReadOnlyList<LibrarySong> Rescan()
    {
        lock (_gate)
        {
            var found = new List<LibrarySong>();
            var unreadable = new List<string>();
            var present = new HashSet<string>(StringComparer.Ordinal);
            var files = Directory.Exists(Root)
                ? Directory.EnumerateFiles(Root, "*", Walk).Where(IsMidi).Take(MaxSongs)
                : [];
            foreach (var path in files)
            {
                present.Add(path);
                var info = new FileInfo(path);
                if (!_seen.TryGetValue(path, out var seen) || seen.Written != info.LastWriteTimeUtc || seen.Length != info.Length)
                {
                    seen = (info.LastWriteTimeUtc, info.Length, Read(path));
                    _seen[path] = seen;
                }

                if (seen.Song is { } song)
                {
                    found.Add(song);
                }
                else
                {
                    unreadable.Add(Id(path));
                }
            }

            foreach (var gone in _seen.Keys.Where(k => !present.Contains(k)).ToArray())
            {
                _seen.Remove(gone);
            }

            _songs = [.. found.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal)];
            _byId = _songs.ToDictionary(s => s.Id, StringComparer.Ordinal);
            Unreadable = unreadable;
            _scannedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            return _songs;
        }
    }

    private static bool IsMidi(string path) =>
        System.IO.Path.GetExtension(path) is var ext
        && (ext.Equals(".mid", StringComparison.OrdinalIgnoreCase) || ext.Equals(".midi", StringComparison.OrdinalIgnoreCase));

    private string Id(string path) => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');

    private LibrarySong? Read(string path)
    {
        try
        {
            var file = MidiFile.Read(path);
            var tempoMap = file.GetTempoMap();
            var duration = file.GetDuration<MetricTimeSpan>();
            var bpm = tempoMap.GetTempoAtTime(new MetricTimeSpan()).BeatsPerMinute;
            return new LibrarySong(Id(path), System.IO.Path.GetFileNameWithoutExtension(path), path, (TimeSpan)duration, bpm);
        }
        catch (Exception ex) when (ex is MidiException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
