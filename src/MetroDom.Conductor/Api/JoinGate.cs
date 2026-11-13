using System.Net;

namespace MetroDom.Conductor.Api;

/// <summary>
/// How often one address may join with a join code. Each join is a fresh guest with fresh request
/// limits, so a phone that clears its storage and scans again would otherwise dodge them. On the
/// venue's network the box sees each phone's own address. Thread-safe.
/// </summary>
internal sealed class JoinGate(TimeProvider time)
{
    /// <summary>Joins one address may make in <see cref="Window"/>: a second browser, a cleared phone.</summary>
    public const int PerAddress = 3;

    /// <summary>The window <see cref="PerAddress"/> counts over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    // Addresses remembered at most; the oldest are forgotten first, so this can't fill memory.
    private const int MaxAddresses = 4096;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _joins = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="address"/> may join now; if not, how long until it may.</summary>
    public bool MayJoin(IPAddress? address, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var recent = Recent(Key(address));
            retryAfter = recent.Count >= PerAddress ? recent[0] + Window - time.GetUtcNow() : TimeSpan.Zero;
            return recent.Count < PerAddress;
        }
    }

    /// <summary>Counts a join <paramref name="address"/> made.</summary>
    public void Joined(IPAddress? address)
    {
        lock (_gate)
        {
            var key = Key(address);
            Recent(key).Add(time.GetUtcNow());
            if (_joins.Count > MaxAddresses)
            {
                var oldest = _joins.MinBy(j => j.Value.Count == 0 ? DateTimeOffset.MinValue : j.Value[^1]).Key;
                _joins.Remove(oldest);
            }
        }
    }

    private static string Key(IPAddress? address) =>
        (address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address)?.ToString() ?? "";

    private List<DateTimeOffset> Recent(string key)
    {
        if (!_joins.TryGetValue(key, out var list))
        {
            list = [];
            _joins[key] = list;
        }

        var since = time.GetUtcNow() - Window;
        list.RemoveAll(t => t <= since);
        return list;
    }
}
