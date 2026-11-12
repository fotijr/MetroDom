namespace MetroDom.Core.Policy;

/// <summary>Who is calling (docs/SECURITY.md). The policy says what each role may do.</summary>
public enum Role
{
    /// <summary>Bar patron, party guest: browse, request, vote. Never panic, settings or live notes.</summary>
    Guest,

    /// <summary>Performer, or the rental customer during their booking.</summary>
    Operator,

    /// <summary>Band owner, rental company, venue owner: everything.</summary>
    Owner,
}

/// <summary>What a Control API caller may do. Each route needs exactly one.</summary>
public enum Grant
{
    /// <summary>See state, queue, instruments and the live stream.</summary>
    View,

    /// <summary>Start, stop, next.</summary>
    Transport,

    /// <summary>Set the tempo, tap.</summary>
    Tempo,

    /// <summary>Vamp and outro.</summary>
    Sections,

    /// <summary>Stop every actuator. Always the operator's, never a guest's.</summary>
    Panic,

    /// <summary>Ask for a song (jukebox): <c>POST /requests</c>. Rules in <see cref="QueueRules"/>.</summary>
    QueueRequest,

    /// <summary>Vote on coming requests (jukebox): <c>POST</c>/<c>DELETE /queue/{id}/vote</c>.</summary>
    QueueVote,

    /// <summary>Add, reorder and remove queued songs directly (POST/DELETE /queue).</summary>
    QueueEdit,

    /// <summary>Hand out and revoke tokens. The owner's alone.</summary>
    Tokens,
}

/// <summary>
/// What operators and guests may do (the policy's <c>roles</c>). The owner can always do
/// everything and isn't configurable. Some rules hold whatever a file says (docs/SECURITY.md):
/// an operator can always panic and view; a guest can only view, request and vote; only the
/// owner hands out tokens.
/// </summary>
public sealed record RolePermissions
{
    private static readonly Dictionary<string, Grant> Names = new(StringComparer.Ordinal)
    {
        ["view"] = Grant.View,
        ["transport"] = Grant.Transport,
        ["tempo"] = Grant.Tempo,
        ["sections"] = Grant.Sections,
        ["panic"] = Grant.Panic,
        ["queue.request"] = Grant.QueueRequest,
        ["queue.vote"] = Grant.QueueVote,
        ["queue.edit"] = Grant.QueueEdit,
        ["tokens"] = Grant.Tokens,
    };

    /// <summary>What a guest may ever be given.</summary>
    public static readonly IReadOnlySet<Grant> GuestCeiling = new HashSet<Grant> { Grant.View, Grant.QueueRequest, Grant.QueueVote };

    /// <summary>What an operator always has.</summary>
    public static readonly IReadOnlySet<Grant> OperatorFloor = new HashSet<Grant> { Grant.View, Grant.Panic };

    /// <summary>
    /// Without a <c>roles</c> section: an operator runs the show and its queue (as every token did
    /// in API v0), guests get nothing.
    /// </summary>
    public static readonly RolePermissions Default = new(
        new HashSet<Grant> { Grant.View, Grant.Transport, Grant.Tempo, Grant.Sections, Grant.Panic, Grant.QueueEdit },
        new HashSet<Grant>());

    public RolePermissions(IReadOnlySet<Grant> operatorCan, IReadOnlySet<Grant> guestCan)
    {
        ArgumentNullException.ThrowIfNull(operatorCan);
        ArgumentNullException.ThrowIfNull(guestCan);
        if (Validate(operatorCan, guestCan) is { } error)
        {
            throw new ArgumentException(error);
        }

        Operator = operatorCan;
        Guest = guestCan;
    }

    public IReadOnlySet<Grant> Operator { get; }

    public IReadOnlySet<Grant> Guest { get; }

    /// <summary>Whether <paramref name="role"/> may do <paramref name="permission"/>.</summary>
    public bool Allows(Role role, Grant permission) => role switch
    {
        Role.Owner => true,
        Role.Operator => Operator.Contains(permission),
        _ => Guest.Contains(permission),
    };

    /// <summary>Everything <paramref name="role"/> may do, in a fixed order.</summary>
    public IReadOnlyList<Grant> For(Role role) => [.. Enum.GetValues<Grant>().Where(p => Allows(role, p))];

    /// <summary>The file name of a permission: "queue.request".</summary>
    public static string NameOf(Grant permission) => Names.First(n => n.Value == permission).Key;

    /// <summary>The file name of a role: "operator".</summary>
    public static string NameOf(Role role) => role.ToString().ToLowerInvariant();

    /// <summary>Reads a permission name as written in a policy file.</summary>
    public static bool TryParse(string name, out Grant permission) => Names.TryGetValue(name, out permission);

    /// <summary>All permission names, for error messages.</summary>
    public static IEnumerable<string> AllNames => Names.Keys;

    /// <summary>Why this pair breaks a rule that holds whatever the file says, or null.</summary>
    public static string? Validate(IReadOnlySet<Grant> operatorCan, IReadOnlySet<Grant> guestCan)
    {
        ArgumentNullException.ThrowIfNull(operatorCan);
        ArgumentNullException.ThrowIfNull(guestCan);
        foreach (var needed in Enum.GetValues<Grant>().Where(OperatorFloor.Contains))
        {
            if (!operatorCan.Contains(needed))
            {
                return $"\"roles.operator\" must include \"{NameOf(needed)}\": an operator can always see the band and stop it.";
            }
        }

        if (operatorCan.Contains(Grant.Tokens) || guestCan.Contains(Grant.Tokens))
        {
            return "only the owner hands out tokens: remove \"tokens\" from the roles.";
        }

        foreach (var extra in Enum.GetValues<Grant>().Where(guestCan.Contains))
        {
            if (!GuestCeiling.Contains(extra))
            {
                return $"guests can't have \"{NameOf(extra)}\": a guest may only view, request and vote (docs/SECURITY.md).";
            }
        }

        return null;
    }
}

/// <summary>
/// The jukebox's rules for guest requests (the policy's <c>queue</c>), enforced by
/// <see cref="Setlist.RequestDesk"/> (exec plan 0004, S3 and S6).
/// </summary>
/// <param name="Requests">Whether guests may ask for songs at all.</param>
/// <param name="Votes">Whether guests may vote queued songs up.</param>
/// <param name="MaxPerGuest">Most songs one guest may have waiting.</param>
/// <param name="Cooldown">Least time between one guest's requests.</param>
/// <param name="Autoplay">
/// Whether a song added to a queue that ran dry starts by itself (a jukebox), instead of waiting
/// for start. Never after a restart: a box that rebooted waits for someone to press start.
/// </param>
public sealed record QueueRules(bool Requests, bool Votes, int MaxPerGuest, TimeSpan Cooldown, bool Autoplay = false)
{
    /// <summary>Least time before a song that played (or was asked for and is waiting) can be requested again.</summary>
    public TimeSpan SongCooldown { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>When the queue runs dry, queue a library song by itself, so the room never goes quiet (<c>serve</c>).</summary>
    public bool Fill { get; init; }

    /// <summary>No requests, no votes: the band plays its own setlist.</summary>
    public static readonly QueueRules Closed = new(false, false, 1, TimeSpan.FromMinutes(5));
}

/// <summary>
/// How long handed-out tokens last (the policy's <c>sessions</c>). A rental customer's token
/// should end with the booking; a guest link with the night.
/// </summary>
/// <param name="MaxTokenLife">Longest life a new operator or guest token may have.</param>
public sealed record SessionRules(TimeSpan MaxTokenLife)
{
    /// <summary>One day.</summary>
    public static readonly SessionRules Default = new(TimeSpan.FromHours(24));

    /// <summary>The longest a token may ever be asked to last: a week.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromDays(7);
}
