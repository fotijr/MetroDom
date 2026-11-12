using System.ComponentModel;
using System.Net;
using MetroDom.Conductor.Api;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Service;
using MetroDom.Core.Policy;
using MetroDom.Core.Setlist;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// The Conductor as a long-running service (plan 0004 S3): one set that never runs out, a song
/// library to queue from, the Control API and web remote always on, tokens and the queue kept
/// across restarts, and the box advertised on the LAN over mDNS. Everything else (outputs,
/// safety, policy, controllers, lights) is <c>play</c>'s.
/// </summary>
internal sealed class ServeCommand : Command<ServeCommand.Settings>
{
    /// <summary>The name phones can type, and the SRV target.</summary>
    public const string HostName = "metrodom.local";

    public sealed class Settings : SetSettings
    {
        [CommandArgument(0, "<LIBRARY>")]
        [Description("Folder of Standard MIDI Files (.mid, .midi, subfolders included) that can be queued from the remote. Rescanned as files change.")]
        public string Library { get; init; } = "";

        [CommandOption("--state <DIR>")]
        [Description("Where the owner token, handed-out tokens (as hashes) and the queue are kept across restarts. Default: ~/.local/state/metrodom (%LOCALAPPDATA%\\MetroDom on Windows).")]
        public string? State { get; init; }

        [CommandOption("--no-mdns")]
        [Description("Don't advertise the box on the LAN (_metrodom._tcp, metrodom.local).")]
        public bool NoMdns { get; init; }

        public string StateFolder => State ?? ServiceState.DefaultFolder();

        /// <summary>Where the API listens: <c>--api</c>, else every interface on port 7890 (phones are the remote).</summary>
        public ApiEndpoint Endpoint => ApiAt ?? new ApiEndpoint(IPAddress.Any, ApiEndpoint.DefaultPort);

        public override ValidationResult Validate()
        {
            if (!Directory.Exists(Library))
            {
                return ValidationResult.Error($"library folder not found: {Library}");
            }

            return base.Validate();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!ServiceState.TryLoad(settings.StateFolder, out var state, out var error))
        {
            throw new UsageException(error!);
        }

        var library = new SongLibrary(settings.Library);
        Ui.Note($"Library {library.Root}: {library.Songs.Count} song{(library.Songs.Count == 1 ? "" : "s")}.");
        if (library.Unreadable.Count > 0)
        {
            Ui.Warn($"Not MIDI files MetroDom can read, left out: {string.Join(", ", library.Unreadable.Take(5))}{(library.Unreadable.Count > 5 ? $" and {library.Unreadable.Count - 5} more" : "")}.");
        }

        var queue = new SetQueue();
        var restored = state.Queue.Select(library.Find).OfType<LibrarySong>().ToArray();
        queue.Replace(restored.Select(s => (s.Id, s.Title)));
        if (restored.Length > 0)
        {
            Ui.Note($"Queue restored: {restored.Length} song{(restored.Length == 1 ? "" : "s")}. The band waits for start.");
        }

        var fromEnv = Environment.GetEnvironmentVariable("METRODOM_API_TOKEN") is { Length: > 0 } set ? set : null;
        var tokens = new TokenStore(fromEnv ?? state.OwnerToken);
        tokens.Import(state.Tokens);
        void Save()
        {
            try
            {
                state.Save(tokens.Export(), Upcoming(queue.Snapshot()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Ui.Warn($"Couldn't save {state.Path}: {ex.Message}");
            }
        }

        Save();
        tokens.Changed += Save;
        queue.Changed += Save;
        return PlayCommand.RunSet(
            settings,
            new SetOptions(queue, song => library.Find(song.Song)?.Path, KeepRunning: true, (live, session) => Start(settings, tokens, library, state, fromEnv is not null, live, session)));
    }

    /// <summary>The songs still to play (the current one included), by library id.</summary>
    internal static IReadOnlyList<string> Upcoming(QueueSnapshot queue) =>
        [.. queue.Songs.Skip(Math.Max(0, queue.Current)).Select(s => s.Song)];

    private static PlayCommand.ApiHandle Start(Settings settings, TokenStore tokens, SongLibrary library, ServiceState state, bool tokenFromEnv, LiveControl live, Session session)
    {
        var endpoint = settings.Endpoint;
        var policy = settings.PolicyProfile;
        var api = ControlApi.Start(endpoint, tokens, policy, live, session, library);
        var link = ControlApi.PairingUrl(api.Address, endpoint, tokenFromEnv ? null : state.OwnerToken);
        Ui.Note($"Control API at {api.Address} (policy {policy.Mode}) · state in {state.Path}");
        Ui.Note(tokenFromEnv
            ? $"Remote: open {link} and paste the owner token from METRODOM_API_TOKEN."
            : $"Remote (owner): open {link}{(Ui.IsInteractive ? " or scan:" : "")}");
        if (!tokenFromEnv && Ui.IsInteractive)
        {
            Ui.Error.Write(new Text(PairingQr.Text(link.ToString())));
        }

        Ui.Note("Invite operators and guests from the remote's Invite panel: each gets a QR code that expires.");
        MdnsAdvertiser? mdns = null;
        if (!endpoint.IsLoopback && !settings.NoMdns)
        {
            var instance = $"MetroDom on {Environment.MachineName}";
            var service = new MdnsService(instance.Length > 63 ? instance[..63] : instance, HostName, (ushort)api.Address.Port, ["path=/", $"api={ControlApi.Prefix}", "v=0", $"mode={policy.Mode}"]);
            mdns = MdnsAdvertiser.TryStart(service, out var mdnsError);
            Ui.Note(mdns is null
                ? $"mDNS off ({mdnsError}): use the link above."
                : $"On this network: http://{HostName}:{api.Address.Port}/ (mDNS, {MdnsService.ServiceType}).");
        }

        if (!endpoint.IsLoopback)
        {
            Ui.Warn("The Control API is open to the network over plain HTTP: anyone who sees a token can use it. Run it on a network you trust (the box's own access point).");
        }

        return new PlayCommand.ApiHandle(api, mdns);
    }
}
