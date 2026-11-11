using System.Globalization;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Core;
using MetroDom.Core.Discovery;

namespace MetroDom.Conductor.Midi;

/// <summary>A device that joins the band: the output, what it plays, and why it was chosen.</summary>
internal sealed record PlannedOutput(MidiPortInfo Port, OutputSpec Spec, InstrumentProfile Profile, string Why);

/// <summary>A device that was found but needs the owner to give it a role.</summary>
internal sealed record AvailableDevice(IdentifiedPort Port, string Why);

internal sealed record BandPlan(IReadOnlyList<PlannedOutput> Members, IReadOnlyList<AvailableDevice> Available);

/// <summary>
/// ADR 0007 step 4: turns what discovery found into a band. A device the owner assigned plays its
/// assignment. A MetroDom instrument that sent its profile joins on its own, on the channel its
/// profile asks for, else the first free one. Everything else (general MIDI devices, devices that
/// don't answer) is only offered: MetroDom never guesses what a stranger's synth should play.
/// </summary>
internal static class AutoBand
{
    /// <param name="ports">What <see cref="Identifier"/> found.</param>
    /// <param name="book">The owner's remembered assignments.</param>
    /// <param name="usedChannels">Channels (0-15) the band already plays, which auto-added instruments avoid.</param>
    /// <param name="inUse">Port names already in the band, skipped.</param>
    public static BandPlan Plan(IReadOnlyList<IdentifiedPort> ports, AssignmentBook book, IEnumerable<int> usedChannels, IReadOnlySet<string>? inUse = null)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(book);
        var used = new HashSet<int>(usedChannels);
        var members = new List<PlannedOutput>();
        var available = new List<AvailableDevice>();
        var fresh = ports.Where(p => inUse is null || !inUse.Contains(p.Output.Name)).ToList();

        // Assignments first: the owner's choices claim their channels before anything is guessed.
        var unassigned = new List<IdentifiedPort>();
        foreach (var port in fresh)
        {
            if (port.Kind != PortKind.Unavailable && book.Find(port.Output.Name, port.Identity) is { } assignment)
            {
                var spec = new OutputSpec(port.Output.Name, assignment.Channels, assignment.Latency, assignment.Notes);
                members.Add(new PlannedOutput(port.Output, spec, SessionOutput.Merge(port.Output.Name, spec, port.Profile), $"remembered: {Describe(assignment)}"));
                used.UnionWith(assignment.Channels);
            }
            else
            {
                unassigned.Add(port);
            }
        }

        foreach (var port in unassigned)
        {
            if (port.Kind == PortKind.MetroDom && port.Profile is { } profile)
            {
                var wanted = profile.Channel is { } c && !used.Contains(c) ? c : (int?)null;
                var channel = wanted ?? Enumerable.Range(0, Orchestra.ChannelCount).Cast<int?>().FirstOrDefault(c => !used.Contains(c!.Value));
                if (channel is not { } chosen)
                {
                    available.Add(new AvailableDevice(port, "MetroDom instrument, but every channel is taken"));
                    continue;
                }

                used.Add(chosen);
                var spec = new OutputSpec(port.Output.Name, [chosen]);
                var why = wanted is null
                    ? profile.Channel is { } asked ? $"channel {asked + 1} was taken" : "first free channel"
                    : "from its profile";
                members.Add(new PlannedOutput(port.Output, spec, SessionOutput.Merge(port.Output.Name, spec, profile), $"MetroDom instrument, channel {chosen + 1} ({why})"));
            }
            else
            {
                available.Add(new AvailableDevice(port, Offer(port)));
            }
        }

        return new BandPlan(members, available);
    }

    /// <summary>"ch 10 · 35 ms · notes 36,38", leaving out what the owner didn't set.</summary>
    public static string Describe(Assignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var text = $"ch {ChannelList.Format(assignment.Channels)}";
        if (assignment.Latency is { } latency)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {latency.TotalMilliseconds:0.#} ms");
        }

        return assignment.Notes is { } notes ? $"{text} · notes {NoteList.Format(notes)}" : text;
    }

    private static string Offer(IdentifiedPort port) => port.Kind switch
    {
        PortKind.MetroDom => $"MetroDom instrument without a profile ({port.ProfileError})",
        PortKind.MidiDevice => port.Identity!.Describe(),
        PortKind.NoReply => "didn't say what it is",
        PortKind.OutputOnly => "output only, can't say what it is",
        _ => $"couldn't ask: {port.Error}",
    };
}

/// <summary>Where the remembered assignments live: a JSON file per user, or wherever the owner says.</summary>
internal static class AssignmentStore
{
    /// <summary>Overrides the default file, for headless boxes and tests.</summary>
    public const string PathVariable = "METRODOM_ASSIGNMENTS";

    /// <summary>The file to use: <paramref name="path"/>, else <c>$METRODOM_ASSIGNMENTS</c>, else the user's app data folder.</summary>
    public static string Resolve(string? path) =>
        path
        ?? (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "metrodom", "assignments.json"));

    /// <summary>Reads the book. A missing file is an empty book; a broken one is reported and treated as empty.</summary>
    public static AssignmentBook Load(string path, Action<string>? warn = null)
    {
        if (!File.Exists(path))
        {
            return new AssignmentBook();
        }

        if (AssignmentBook.TryParse(File.ReadAllText(path), out var book, out var error))
        {
            return book;
        }

        warn?.Invoke($"Ignoring the remembered assignments in {path}: {error}");
        return new AssignmentBook();
    }

    /// <summary>Writes the book, replacing the file in one step so a crash never leaves half of it.</summary>
    public static void Save(AssignmentBook book, string path)
    {
        ArgumentNullException.ThrowIfNull(book);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        File.WriteAllText(temp, book.ToJson());
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Notices outputs that appear or disappear (hot-plug) by listing them every second, and asks
/// each new one who it is, once. <see cref="Arrived"/> and <see cref="Left"/> fire on the timer's
/// thread. Ports <c>ignore</c> says are already handled (the band's own outputs coming back after
/// an unplug, which <see cref="ResilientOutput"/> reconnects) aren't asked.
/// </summary>
internal sealed class DeviceWatcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly IMidiBackend _backend;
    private readonly TimeSpan _timeout;
    private readonly Func<string, bool> _ignore;
    private readonly HashSet<string> _known;
    private readonly Lock _polling = new();
    private readonly ITimer _timer;
    private int _failures;

    public DeviceWatcher(IMidiBackend backend, IEnumerable<string> present, Func<string, bool> ignore, TimeSpan? timeout = null, TimeSpan? every = null, TimeProvider? time = null)
    {
        _backend = backend;
        _ignore = ignore;
        _timeout = timeout ?? Identifier.DefaultTimeout;
        _known = new HashSet<string>(present, StringComparer.Ordinal);
        var interval = every ?? DefaultInterval;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Poll(), null, interval, interval);
    }

    /// <summary>A new output and what it said it is.</summary>
    public event Action<IdentifiedPort>? Arrived;

    /// <summary>An output that went away (by name).</summary>
    public event Action<string>? Left;

    /// <summary>
    /// Listing or asking failed on two looks in a row (a single failure is normal while a device
    /// is mid-unplug). Raised once per run of failures; the watcher keeps going.
    /// </summary>
    public event Action<Exception>? Failed;

    /// <summary>One look at the ports. Skipped if the last one is still asking.</summary>
    internal void Poll()
    {
        if (!_polling.TryEnter())
        {
            return;
        }

        try
        {
            var outputs = _backend.Outputs();
            var names = outputs.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _known.Where(n => !names.Contains(n)).ToList())
            {
                _known.Remove(gone);
                Left?.Invoke(gone);
            }

            var added = outputs.Where(o => _known.Add(o.Name) && !_ignore(o.Name)).ToList();
            if (added.Count > 0)
            {
                foreach (var port in Identifier.Identify(_backend, _timeout, added))
                {
                    Arrived?.Invoke(port);
                }
            }

            _failures = 0;
        }
#pragma warning disable CA1031 // A flaky driver must not kill the watcher (or the set).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (++_failures == 2)
            {
                Failed?.Invoke(ex);
            }
        }
        finally
        {
            _polling.Exit();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();

        // Wait out a poll in progress, so nothing fires after Dispose returns.
        lock (_polling)
        {
        }
    }
}
