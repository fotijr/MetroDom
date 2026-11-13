using System.Globalization;
using MetroDom.Core.Policy;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Setlist;

/// <summary>Why a request or vote was turned down, so the API can answer with the right status.</summary>
public enum Refusal
{
    /// <summary>Requests or votes are off (policy) or paused (owner).</summary>
    Closed,

    /// <summary>A guest limit: songs waiting, time between requests, or too many tries. Try later.</summary>
    TooSoon,

    /// <summary>The song itself can't be asked for now: waiting already, or played recently.</summary>
    Conflict,

    /// <summary>No such queued song.</summary>
    NotFound,
}

/// <summary>A decision on a guest's request or vote.</summary>
/// <param name="Song">The queued song it made or counted toward, when accepted.</param>
/// <param name="CountedAsVote">The song was already requested, so the request became a vote.</param>
/// <param name="Refused">Why not, when refused.</param>
/// <param name="Message">What to tell the guest, either way.</param>
/// <param name="RetryAfter">For <see cref="Refusal.TooSoon"/>: when trying again can work.</param>
public sealed record DeskReply(QueuedSong? Song, bool CountedAsVote, Refusal? Refused, string Message, TimeSpan? RetryAfter = null)
{
    public bool Accepted => Refused is null;
}

/// <summary>One guest's standing, for the remote to show before they ask.</summary>
public sealed record GuestStatus(int Waiting, int MaxWaiting, TimeSpan? NextRequestIn);

/// <summary>
/// The jukebox's front desk (plan 0004 S6, the crowd-jukebox spec): every guest request and vote
/// goes through here and the policy's <c>queue</c> rules are enforced. Guests are untrusted
/// (SECURITY.md): each holder id is limited in songs waiting, in time between requests and in
/// tries per minute (refused tries count too, so a flood stays a flood), the same song can't be
/// asked for again while it waits or soon after it played, and each holder votes once per song.
/// Owners and operators skip the guest limits (they edit the queue anyway) but not the song
/// rules. Pure logic on an <see cref="IClock"/>; thread-safe.
/// </summary>
public sealed class RequestDesk
{
    /// <summary>Tries (requests and votes, accepted or not) one holder may make per minute.</summary>
    public const int TriesPerMinute = 20;

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly SetQueue _queue;
    private readonly IClock _clock;
    private readonly Dictionary<string, TimeSpan> _lastRequest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<TimeSpan>> _tries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _lastPlayed = new(StringComparer.Ordinal);
    private string? _nowPlaying;
    private bool _paused;

    public RequestDesk(SetQueue queue, QueueRules rules, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(clock);
        _queue = queue;
        Rules = rules;
        _clock = clock;
        _queue.Changed += Remember;
        Remember();
    }

    public QueueRules Rules { get; }

    /// <summary>Whether guests can ask for songs right now: the policy allows it and the owner hasn't paused it.</summary>
    public bool RequestsOpen
    {
        get
        {
            lock (_gate)
            {
                return Rules.Requests && !_paused;
            }
        }
    }

    public bool VotesOpen => Rules.Votes && RequestsOpen;

    /// <summary>Raised when requests are paused or reopened.</summary>
    public event Action? Changed;

    /// <summary>The owner's switch (policy can't be overridden: closed stays closed). Returns whether anything changed.</summary>
    public bool Pause(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
            {
                return false;
            }

            _paused = paused;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// <paramref name="holder"/> (an anonymous id, the same for one token) asks for
    /// <paramref name="song"/>. <paramref name="guest"/> says whether the guest limits apply.
    /// </summary>
    public DeskReply Request(string holder, bool guest, string song, string title, string? shownAs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        var now = _clock.Now;
        lock (_gate)
        {
            if (!Rules.Requests || _paused)
            {
                return Refuse(Refusal.Closed, Rules.Requests ? "Requests are paused for now." : "This band doesn't take requests.");
            }

            if (guest && Flooding(holder, now) is { } wait)
            {
                return Refuse(Refusal.TooSoon, "Too many tries: wait a minute.", wait);
            }

            var coming = Coming();
            if (coming.FirstOrDefault(s => s.Song == song) is { } waiting)
            {
                if (waiting.Kind == SongKind.Request && Rules.Votes && !waiting.Voters.Contains(holder))
                {
                    return _queue.TryVote(waiting.Id, holder, up: true, out var voted, out var voteError)
                        ? new DeskReply(voted, CountedAsVote: true, null, $"{title} was already requested: counted your vote ({voted.Votes}).")
                        : Refuse(Refusal.Conflict, Capitalize(voteError!));
                }

                return Refuse(Refusal.Conflict, $"{title} is already in the queue.");
            }

            if (_queue.Now?.Song == song)
            {
                return Refuse(Refusal.Conflict, $"{title} is up now.");
            }

            if (_lastPlayed.TryGetValue(song, out var played) && now - played < Rules.SongCooldown)
            {
                var again = Rules.SongCooldown - (now - played);
                return Refuse(Refusal.Conflict, string.Create(CultureInfo.InvariantCulture, $"{title} played {Minutes(now - played)} ago; it can be asked for again in {Minutes(again)}."));
            }

            if (guest)
            {
                var mine = coming.Count(s => s.Kind == SongKind.Request && s.RequestedBy == holder);
                if (mine >= Rules.MaxPerGuest)
                {
                    return Refuse(Refusal.TooSoon, Rules.MaxPerGuest == 1
                        ? "Your request is still waiting: ask again once it has played."
                        : string.Create(CultureInfo.InvariantCulture, $"You have {mine} requests waiting: ask again once one has played."));
                }

                if (_lastRequest.TryGetValue(holder, out var last) && now - last < Rules.Cooldown)
                {
                    var left = Rules.Cooldown - (now - last);
                    return Refuse(Refusal.TooSoon, $"One request every {Minutes(Rules.Cooldown)}: try again in {Minutes(left)}.", left);
                }
            }

            if (!_queue.TryRequest(song, title, shownAs, holder, out var added, out var error))
            {
                return Refuse(Refusal.Conflict, Capitalize(error!));
            }

            _lastRequest[holder] = now;
            return new DeskReply(added, CountedAsVote: false, null, $"Requested {title}.");
        }
    }

    /// <summary><paramref name="holder"/> votes for (or, with <paramref name="up"/> false, un-votes) the coming request <paramref name="id"/>.</summary>
    public DeskReply Vote(string holder, bool guest, string id, bool up)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        var now = _clock.Now;
        lock (_gate)
        {
            if (!Rules.Votes || !Rules.Requests || _paused)
            {
                return Refuse(Refusal.Closed, Rules.Votes ? "Requests are paused for now." : "This band doesn't take votes.");
            }

            if (guest && Flooding(holder, now) is { } wait)
            {
                return Refuse(Refusal.TooSoon, "Too many tries: wait a minute.", wait);
            }

            if (!_queue.Snapshot().Songs.Any(s => s.Id == id))
            {
                return Refuse(Refusal.NotFound, $"No song {id} in the queue.");
            }

            return _queue.TryVote(id, holder, up, out var song, out var error)
                ? new DeskReply(song, CountedAsVote: up, null, up ? $"Voted for {song.Title} ({song.Votes})." : $"Took back your vote for {song.Title}.")
                : Refuse(Refusal.Conflict, Capitalize(error!));
        }
    }

    /// <summary>Where <paramref name="holder"/> stands: requests waiting and how long until they may ask again.</summary>
    public GuestStatus Status(string holder)
    {
        var now = _clock.Now;
        lock (_gate)
        {
            var waiting = Coming().Count(s => s.Kind == SongKind.Request && s.RequestedBy == holder);
            TimeSpan? next = _lastRequest.TryGetValue(holder, out var last) && now - last < Rules.Cooldown ? Rules.Cooldown - (now - last) : null;
            return new GuestStatus(waiting, Rules.MaxPerGuest, next);
        }
    }

    /// <summary>
    /// A song to fill the silence when the queue ran dry (policy <c>fill</c>): one of
    /// <paramref name="library"/> that hasn't played within the song cooldown, picked at random;
    /// else the one that played longest ago. Null for an empty library.
    /// </summary>
    public string? PickFill(IReadOnlyList<string> library, Random random)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(random);
        if (library.Count == 0)
        {
            return null;
        }

        var now = _clock.Now;
        lock (_gate)
        {
            var rested = library.Where(s => !_lastPlayed.TryGetValue(s, out var at) || now - at >= Rules.SongCooldown).ToArray();
            return rested.Length > 0
                ? rested[random.Next(rested.Length)]
                : library.MinBy(s => _lastPlayed.GetValueOrDefault(s, TimeSpan.MinValue));
        }
    }

    /// <summary>When the song up now changes, it counts as played (for the song cooldown).</summary>
    private void Remember()
    {
        var now = _queue.Now;
        lock (_gate)
        {
            if (now is not null && now.Id != _nowPlaying)
            {
                _lastPlayed[now.Song] = _clock.Now;
            }

            _nowPlaying = now?.Id;
        }
    }

    private List<QueuedSong> Coming()
    {
        var snapshot = _queue.Snapshot();
        return [.. snapshot.Songs.Skip(snapshot.EditableFrom)];
    }

    /// <summary>Counts a try; if there were too many in the last minute, how long until one frees up.</summary>
    private TimeSpan? Flooding(string holder, TimeSpan now)
    {
        if (!_tries.TryGetValue(holder, out var tries))
        {
            _tries[holder] = tries = new Queue<TimeSpan>();
        }

        while (tries.Count > 0 && now - tries.Peek() >= Minute)
        {
            tries.Dequeue();
        }

        if (tries.Count >= TriesPerMinute)
        {
            return Minute - (now - tries.Peek());
        }

        tries.Enqueue(now);
        return null;
    }

    private static DeskReply Refuse(Refusal why, string message, TimeSpan? retryAfter = null) => new(null, false, why, message, retryAfter);

    private static string Minutes(TimeSpan t) =>
        t < Minute
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Ceiling(t.TotalSeconds)):0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(t.TotalMinutes):0} min");

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
