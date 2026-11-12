namespace MetroDom.Core.Setlist;

/// <summary>One song in the queue.</summary>
/// <param name="Id">Short id, stable while it's queued (moves and removes name it, not its index).</param>
/// <param name="Song">What to play: a library id, or a file path for <c>play</c>.</param>
/// <param name="Title">What people see.</param>
/// <param name="AddedBy">Who queued it (a token label or role), or null for the set it started with.</param>
public sealed record QueuedSong(string Id, string Song, string Title, string? AddedBy);

/// <summary>A moment's view of the queue.</summary>
/// <param name="Current">The song playing or up next; -1 before the set; <c>Songs.Count</c> when it ran dry.</param>
/// <param name="Songs">Played songs (kept as recent history), the current one, then what's coming.</param>
/// <param name="EditableFrom">The first index that may be moved, removed or inserted before.</param>
/// <param name="Version">Counts every change, so clients can tell when to redraw.</param>
/// <param name="Dropped">Played songs let go from the front (beyond <see cref="SetQueue.MaxHistory"/>), so "song 23 of 30" stays true.</param>
public sealed record QueueSnapshot(int Current, IReadOnlyList<QueuedSong> Songs, int EditableFrom, long Version, int Dropped = 0)
{
    /// <summary>The song playing or up next, or null.</summary>
    public QueuedSong? Now => Current >= 0 && Current < Songs.Count ? Songs[Current] : null;
}

/// <summary>
/// The set as an editable queue (ADR 0005, plan 0004 S3): what's been played, what's playing or
/// up next, and what's coming. Only what's coming can be edited: the song playing (or waiting
/// for start) is the band's, and history is history; skipping is <c>next</c>, not a delete.
/// Pure logic, thread-safe; the Conductor's set loop and the Control API share one.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It is the band's queue: the word the API, the app and people use.")]
public sealed class SetQueue
{
    /// <summary>A long night's worth; enough for any party, small enough to send on every change.</summary>
    public const int MaxSongs = 200;

    /// <summary>Played songs kept for people to look back at.</summary>
    public const int MaxHistory = 20;

    private readonly Lock _gate = new();
    private readonly List<QueuedSong> _songs = [];
    private int _current = -1;
    private long _version;
    private long _nextId;
    private int _dropped;

    /// <summary>After any change, outside the lock.</summary>
    public event Action? Changed;

    /// <summary>The queue now.</summary>
    public QueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new QueueSnapshot(_current, [.. _songs], EditableFrom, _version, _dropped);
        }
    }

    /// <summary>The song playing or up next, or null (before the set, or it ran dry).</summary>
    public QueuedSong? Now
    {
        get
        {
            lock (_gate)
            {
                return _current >= 0 && _current < _songs.Count ? _songs[_current] : null;
            }
        }
    }

    /// <summary>Whether the set has played everything queued.</summary>
    public bool IsDry
    {
        get
        {
            lock (_gate)
            {
                return _current >= _songs.Count;
            }
        }
    }

    // Before the set everything can change; once it ran dry, a new song becomes the next one up.
    private int EditableFrom => _current < 0 ? 0 : _current >= _songs.Count ? _songs.Count : _current + 1;

    /// <summary>Replaces everything with <paramref name="songs"/> (song, title), before the set starts.</summary>
    public void Replace(IEnumerable<(string Song, string Title)> songs)
    {
        ArgumentNullException.ThrowIfNull(songs);
        lock (_gate)
        {
            _songs.Clear();
            foreach (var (song, title) in songs.Take(MaxSongs))
            {
                _songs.Add(new QueuedSong(NewId(), song, title, null));
            }

            _current = -1;
            _dropped = 0;
            _version++;
        }

        Changed?.Invoke();
    }

    /// <summary>Marks song <paramref name="index"/> as the one waiting or playing.</summary>
    public void Cue(int index)
    {
        lock (_gate)
        {
            _current = Math.Clamp(index, -1, _songs.Count);
            _version++;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Moves on to the next song (it ended, or someone pressed next). Old history is let go.
    /// Returns the new current song, or null when the queue ran dry.
    /// </summary>
    public QueuedSong? Advance()
    {
        QueuedSong? now;
        lock (_gate)
        {
            _current = Math.Min(_current + 1, _songs.Count);
            var drop = _current - MaxHistory;
            if (drop > 0)
            {
                _songs.RemoveRange(0, drop);
                _current -= drop;
                _dropped += drop;
            }

            now = _current < _songs.Count ? _songs[_current] : null;
            _version++;
        }

        Changed?.Invoke();
        return now;
    }

    /// <summary>
    /// Queues <paramref name="song"/> at <paramref name="at"/> (an index, at least
    /// <see cref="QueueSnapshot.EditableFrom"/>), or at the end. Errors say what to change.
    /// </summary>
    public bool TryAdd(string song, string title, string? addedBy, int? at, out QueuedSong added, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        added = null!;
        lock (_gate)
        {
            if (_songs.Count - Math.Max(0, _current) >= MaxSongs)
            {
                error = $"the queue is full ({MaxSongs} songs).";
                return false;
            }

            var index = at ?? _songs.Count;
            if (index < EditableFrom || index > _songs.Count)
            {
                error = $"at must be {EditableFrom}-{_songs.Count} (only what's coming can change).";
                return false;
            }

            added = new QueuedSong(NewId(), song, title, addedBy);
            _songs.Insert(index, added);
            _version++;
        }

        error = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves a coming song to index <paramref name="to"/> (in the queue as it is now).</summary>
    public bool TryMove(string id, int to, out string? error)
    {
        lock (_gate)
        {
            var from = _songs.FindIndex(s => s.Id == id);
            if (from < 0)
            {
                error = $"no song {id} in the queue.";
                return false;
            }

            var first = EditableFrom;
            if (from < first)
            {
                error = "that song has played or is playing: only what's coming can move.";
                return false;
            }

            if (to < first || to >= _songs.Count)
            {
                error = $"to must be {first}-{_songs.Count - 1}.";
                return false;
            }

            var song = _songs[from];
            _songs.RemoveAt(from);
            _songs.Insert(to, song);
            _version++;
        }

        error = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Takes a coming song out of the queue.</summary>
    public bool TryRemove(string id, out string? error)
    {
        lock (_gate)
        {
            var index = _songs.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                error = $"no song {id} in the queue.";
                return false;
            }

            if (index < EditableFrom)
            {
                error = "that song has played or is playing: skip it with next instead.";
                return false;
            }

            _songs.RemoveAt(index);
            _version++;
        }

        error = null;
        Changed?.Invoke();
        return true;
    }

    private string NewId() => (++_nextId).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}
