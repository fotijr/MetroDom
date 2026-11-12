using System.Security.Cryptography;
using System.Text;
using MetroDom.Core.Policy;

namespace MetroDom.Conductor.Api;

/// <summary>Who a token belongs to: its role and, for handed-out tokens, its id, label and end.</summary>
/// <param name="Id">"owner" for the owner token, else a short public id (safe to show and log).</param>
/// <param name="Role">What the policy lets it do.</param>
/// <param name="Label">What the owner called it ("Smith party"), or null.</param>
/// <param name="Issued">When it was handed out (null for the owner token).</param>
/// <param name="Expires">When it stops working (null for the owner token: it lasts the run).</param>
internal sealed record TokenHolder(string Id, Role Role, string? Label, DateTimeOffset? Issued, DateTimeOffset? Expires)
{
    public const string OwnerId = "owner";
}

/// <summary>
/// The Control API's tokens (docs/SECURITY.md): one owner token for the run, and operator or
/// guest tokens the owner hands out, each ending when its booking or night does. Handed-out
/// tokens are kept only as SHA-256 hashes and compared in constant time. In <c>play</c> they
/// live in memory; <c>serve</c> saves them (<see cref="Export"/>, <see cref="Changed"/>) so a
/// restart doesn't log every phone out. Thread-safe.
/// </summary>
internal sealed class TokenStore
{
    /// <summary>More than any party needs; a cap so a leaked owner token can't fill memory.</summary>
    public const int MaxIssued = 256;

    /// <summary>Longest a label may be.</summary>
    public const int MaxLabel = 60;

    private readonly Lock _gate = new();
    private readonly byte[] _owner;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (TokenHolder Holder, byte[] Hash)> _issued = new(StringComparer.Ordinal);

    public TokenStore(string ownerToken, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);
        _owner = Encoding.UTF8.GetBytes(ownerToken);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>After a token is handed out or revoked (not when one expires), outside the lock.</summary>
    public event Action? Changed;

    /// <summary>Handed-out tokens still working, with their hashes, for saving.</summary>
    public IReadOnlyList<(TokenHolder Holder, byte[] Hash)> Export()
    {
        lock (_gate)
        {
            Prune();
            return [.. _issued.Values.OrderBy(v => v.Holder.Expires)];
        }
    }

    /// <summary>Takes back saved tokens (expired ones, owner ones and bad hashes are dropped).</summary>
    public void Import(IEnumerable<(TokenHolder Holder, byte[] Hash)> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        lock (_gate)
        {
            foreach (var (holder, hash) in saved)
            {
                if (holder.Role != Role.Owner && holder.Expires is not null && hash.Length == SHA256.HashSizeInBytes
                    && holder.Id != TokenHolder.OwnerId && _issued.Count < MaxIssued)
                {
                    _issued[holder.Id] = (holder, hash);
                }
            }

            Prune();
        }
    }

    /// <summary>The token's holder, or null if it's wrong, revoked or expired.</summary>
    public TokenHolder? Find(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(presented);
        if (CryptographicOperations.FixedTimeEquals(bytes, _owner))
        {
            return new TokenHolder(TokenHolder.OwnerId, Role.Owner, null, null, null);
        }

        var hash = SHA256.HashData(bytes);
        lock (_gate)
        {
            Prune();
            TokenHolder? found = null;
            foreach (var (holder, stored) in _issued.Values)
            {
                // Check every one, so the time taken doesn't say which matched.
                if (CryptographicOperations.FixedTimeEquals(hash, stored))
                {
                    found = holder;
                }
            }

            return found;
        }
    }

    /// <summary>Whether <paramref name="holder"/>'s token still works (not revoked, not expired).</summary>
    public bool IsValid(TokenHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (holder.Role == Role.Owner)
        {
            return true;
        }

        lock (_gate)
        {
            Prune();
            return _issued.ContainsKey(holder.Id);
        }
    }

    /// <summary>
    /// Hands out a token for <paramref name="role"/> lasting <paramref name="life"/>. Returns the
    /// secret once; only its hash is kept. Errors say what to change.
    /// </summary>
    public bool TryIssue(Role role, TimeSpan life, string? label, SessionRules rules, out TokenHolder holder, out string secret, out string? error)
    {
        ArgumentNullException.ThrowIfNull(rules);
        holder = null!;
        secret = "";
        if (role == Role.Owner)
        {
            error = "there is one owner token; hand out operator or guest tokens.";
            return false;
        }

        if (life <= TimeSpan.Zero || life > rules.MaxTokenLife)
        {
            error = $"a token may last more than 0 and at most {rules.MaxTokenLife.TotalHours:0.#} hours under this policy.";
            return false;
        }

        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is { Length: > MaxLabel } || (label is not null && label.Any(char.IsControl)))
        {
            error = $"the label must be at most {MaxLabel} characters, without control characters.";
            return false;
        }

        lock (_gate)
        {
            Prune();
            if (_issued.Count >= MaxIssued)
            {
                error = $"{MaxIssued} tokens are already out: revoke some first.";
                return false;
            }

            var now = _time.GetUtcNow();
            string id;
            do
            {
                id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            }
            while (_issued.ContainsKey(id) || id == TokenHolder.OwnerId);

            secret = ControlApi.NewToken();
            holder = new TokenHolder(id, role, label, now, now + life);
            _issued[id] = (holder, SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        }

        error = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Stops a handed-out token working now. False if there's no such token.</summary>
    public bool Revoke(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _issued.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>Tokens out now, soonest to end first.</summary>
    public IReadOnlyList<TokenHolder> Issued()
    {
        lock (_gate)
        {
            Prune();
            return [.. _issued.Values.Select(v => v.Holder).OrderBy(h => h.Expires)];
        }
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var id in _issued.Where(i => i.Value.Holder.Expires <= now).Select(i => i.Key).ToArray())
        {
            _issued.Remove(id);
        }
    }
}
