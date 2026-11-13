using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Service;
using MetroDom.Core.Policy;
using MetroDom.Core.Setlist;
using MetroDom.Core.Timing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MetroDom.Conductor.Api;

/// <summary>Where the Control API listens: <c>PORT</c> (loopback only) or <c>HOST:PORT</c>.</summary>
internal sealed record ApiEndpoint(IPAddress Address, int Port)
{
    public const int DefaultPort = 7890;

    public bool IsLoopback => IPAddress.IsLoopback(Address);

    /// <summary>Parses <c>7890</c>, <c>127.0.0.1:7890</c>, <c>0.0.0.0:7890</c>, <c>[::]:7890</c>.</summary>
    public static bool TryParse(string? raw, out ApiEndpoint endpoint, out string? error)
    {
        endpoint = new ApiEndpoint(IPAddress.Loopback, DefaultPort);
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        raw = raw.Trim();
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            endpoint = endpoint with { Port = port };
        }
        else if (IPEndPoint.TryParse(raw, out var parsed) && raw.Contains(':', StringComparison.Ordinal) && parsed.Port > 0)
        {
            endpoint = new ApiEndpoint(parsed.Address, parsed.Port);
        }
        else
        {
            error = $"'{raw}' is not PORT or HOST:PORT (e.g. 7890 or 0.0.0.0:7890).";
            return false;
        }

        if (endpoint.Port is < 1 or > 65535)
        {
            error = $"port {endpoint.Port} is not 1-65535.";
            return false;
        }

        return true;
    }

    public override string ToString() =>
        Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{Address}]:{Port}" : $"{Address}:{Port}";
}

/// <summary>What a route needs: a grant, or (null) only a token that works.</summary>
internal sealed record Requires(Grant? Grant);

/// <summary>
/// The Control API v0 (ADR 0003): HTTP + JSON at <c>/api/v0</c> on top of a running set's
/// <see cref="LiveControl"/>. Every route but the spec needs a token, and the token's role must
/// have the route's grant under the policy (ADR 0005, docs/SECURITY.md); a route that declares
/// nothing is refused. Routes and the checked-in <c>openapi.json</c> are kept equal by a test.
/// See docs/references/control-api.md.
/// </summary>
internal sealed class ControlApi : IAsyncDisposable
{
    public const string Prefix = "/api/v0";

    /// <summary>Requests are tiny intents; anything bigger is a mistake or an attack.</summary>
    private const long MaxBodyBytes = 4096;

    private readonly WebApplication _app;
    private int _disposed;

    private ControlApi(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>Where it actually listens (the real port when started on port 0).</summary>
    public Uri Address { get; }

    /// <summary>The checked-in spec, embedded in the binary.</summary>
    public static string Spec() => Resource("openapi.json");

    /// <summary>The checked-in JSON Schema of the stream's events.</summary>
    public static string EventSchema() => Resource("events.schema.json");

    private static byte[] ResourceBytes(string name)
    {
        using var stream = typeof(ControlApi).Assembly.GetManifestResourceStream($"MetroDom.Conductor.Api.{name}")
            ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string Resource(string name)
    {
        using var stream = typeof(ControlApi).Assembly.GetManifestResourceStream($"MetroDom.Conductor.Api.{name}")
            ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>A random token: 128 bits, URL-safe.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Starts listening with one owner token and the live-band policy. Throws if the port is taken.</summary>
    public static ControlApi Start(ApiEndpoint endpoint, string ownerToken, LiveControl live, Session session) =>
        Start(endpoint, new TokenStore(ownerToken), PolicyProfile.LiveBand, live, session);

    /// <summary>Starts listening. With a <paramref name="library"/> (serve), songs can be queued from it. Throws if the port is taken.</summary>
    public static ControlApi Start(ApiEndpoint endpoint, TokenStore tokens, PolicyProfile policy, LiveControl live, Session session, SongLibrary? library = null, RequestDesk? desk = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(session);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // The terminal belongs to the live view.
        // The host must not own Ctrl+C / SIGTERM: they stop the whole set (Session), not just the API.
        builder.Services.AddSingleton<IHostLifetime, NoSignals>();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(endpoint.Address, endpoint.Port);
            options.Limits.MaxRequestBodySize = MaxBodyBytes;
            options.AddServerHeader = false;
        });
        var app = builder.Build();
        Map(app, endpoint, tokens, policy, live, session, library, desk);
        app.StartAsync().GetAwaiter().GetResult();

        var bound = app.Urls.Select(u => new Uri(u)).First();
        return new ControlApi(app, new Uri($"http://{endpoint.Address.ToUriHost()}:{bound.Port}{Prefix}/"));
    }

    /// <summary>The routes this server answers.</summary>
    internal IEnumerable<(string Method, string Path)> Routes() => Routes(_app);

    /// <summary>Each guarded route and the grant it declares ("" for any working token, null for none declared).</summary>
    internal IEnumerable<(string Method, string Path, string? Grant)> Grants() =>
        ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.TrimStart('/').StartsWith(Prefix.TrimStart('/') + "/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (
                m,
                ("/" + e.RoutePattern.RawText!.TrimStart('/'))[Prefix.Length..],
                e.Metadata.GetMetadata<Requires>() is { } r ? (r.Grant is { } g ? RolePermissions.NameOf(g) : "") : (string?)null)));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The routes (method, path under <see cref="Prefix"/>) the server answers, for the spec test.</summary>
    internal static IEnumerable<(string Method, string Path)> Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (m, "/" + e.RoutePattern.RawText!.TrimStart('/'))))
            .Where(r => r.Item2.StartsWith(Prefix + "/", StringComparison.Ordinal))
            .Select(r => (r.m, r.Item2[Prefix.Length..]));

    internal static void Map(WebApplication app, ApiEndpoint endpoint, TokenStore tokens, PolicyProfile policy, LiveControl live, Session session, SongLibrary? library, RequestDesk? desk = null)
    {
        var spec = Spec();
        var schema = EventSchema();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        MapWebApp(app);
        var api = app.MapGroup(Prefix);
        api.MapGet("/openapi.json", () => Results.Text(spec, "application/json"));
        api.MapGet("/events.schema.json", () => Results.Text(schema, "application/json"));

        var guarded = api.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (tokens.Find(PresentedToken(http.Request)) is not { } holder)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Unauthorized();
            }

            // Fail closed: a route that forgot to say what it needs is refused to everyone.
            if (http.GetEndpoint()?.Metadata.GetMetadata<Requires>() is not { } requires)
            {
                return Forbidden("This route declares no permission, so nobody may use it.");
            }

            // A join code hangs on a public screen: it only shows the band. Phones scan it to join.
            if (holder.Door && requires.Grant is not (null or Grant.View))
            {
                return Forbidden("A join code only shows the band: scan it with a phone to join as a guest.");
            }

            if (requires.Grant is { } grant && !policy.Roles.Allows(holder.Role, grant))
            {
                return Forbidden($"A{(holder.Role == Role.Operator ? "n" : "")} {RolePermissions.NameOf(holder.Role)} can't do this under the {policy.Mode} policy (needs \"{RolePermissions.NameOf(grant)}\").");
            }

            http.Items[HolderKey] = holder;
            return await next(context).ConfigureAwait(false);
        });

        guarded.MapGet("/session", (HttpContext context) =>
        {
            var holder = Holder(context);
            return Results.Json(new
            {
                id = holder.Id,
                role = RolePermissions.NameOf(holder.Role),
                label = holder.Label,
                expiresAt = holder.Expires,
                mode = policy.Mode,
                grants = policy.Roles.For(holder.Role).Where(g => !holder.Door || g == Grant.View).Select(RolePermissions.NameOf),
                join = holder.Door,
            });
        }).Needs(null);
        guarded.MapGet("/state", () => Results.Json(State(live.Snapshot()))).Needs(Grant.View);
        var stream = new StateStream(live, session, desk);
        guarded.MapGet("/stream", (Delegate)((HttpContext context) =>
        {
            var holder = Holder(context);
            return stream.Accept(context, () => tokens.IsValid(holder), app.Lifetime.ApplicationStopping, holder.Id);
        })).Needs(Grant.View);
        guarded.MapGet("/queue", (HttpContext context) => Results.Json(Queue(live.Queue.Snapshot(), desk, Holder(context).Id))).Needs(Grant.View);

        // Guest requests and votes (plan 0004 S6): every rule lives in the RequestDesk.
        guarded.MapGet("/requests", (HttpContext context) =>
        {
            if (desk is null)
            {
                return Results.Json(new { open = false, votes = false });
            }

            var you = desk.Status(Holder(context).Id);
            return Results.Json(new
            {
                open = desk.RequestsOpen,
                votes = desk.VotesOpen,
                maxPerGuest = desk.Rules.MaxPerGuest,
                cooldownSeconds = desk.Rules.Cooldown.TotalSeconds,
                songCooldownMinutes = desk.Rules.SongCooldown.TotalMinutes,
                you = new { waiting = you.Waiting, maxWaiting = you.MaxWaiting, nextRequestInSeconds = you.NextRequestIn is { } next ? Math.Ceiling(next.TotalSeconds) : (double?)null },
            });
        }).Needs(Grant.View);
        guarded.MapPut("/requests", (OpenRequest? request) =>
        {
            if (desk is null || !desk.Rules.Requests)
            {
                return Conflict("This policy takes no requests (queue.requests is false).");
            }

            if (request?.Open is not { } open)
            {
                return BadRequest("open is required: true to take requests, false to pause them.");
            }

            if (desk.Pause(!open))
            {
                session.Messages.Add(open ? "Requests are open again." : "Requests paused.");
            }

            return Results.Json(new { message = open ? "Requests are open." : "Requests are paused.", open });
        }).Needs(Grant.QueueEdit);
        guarded.MapPost("/requests", (HttpContext context, QueueRequest? request) =>
        {
            if (desk is null || library is null)
            {
                return Conflict("This set takes no requests: run metrodom serve with a jukebox policy.");
            }

            if (string.IsNullOrWhiteSpace(request?.Song) || request.Song.Length > 512)
            {
                return BadRequest("song is required: a library id from GET /library.");
            }

            if (library.Find(request.Song) is not { } song)
            {
                return NotFound($"No song \"{request.Song}\" in the library: GET {Prefix}/library lists them.");
            }

            var holder = Holder(context);
            var reply = desk.Request(holder.Id, holder.Role == Role.Guest, song.Id, song.Title, holder.Label ?? RolePermissions.NameOf(holder.Role));
            if (reply is { Accepted: true, CountedAsVote: false })
            {
                session.Messages.Add($"{holder.Label ?? "A guest"} requested {song.Title}.");
            }

            return Reply(context, reply, live.Queue.Snapshot(), desk, holder.Id);
        }).Needs(Grant.QueueRequest);
        guarded.MapPost("/queue/{id}/vote", (HttpContext context, string id) => Vote(context, id, up: true)).Needs(Grant.QueueVote);
        guarded.MapDelete("/queue/{id}/vote", (HttpContext context, string id) => Vote(context, id, up: false)).Needs(Grant.QueueVote);

        IResult Vote(HttpContext context, string id, bool up)
        {
            if (desk is null)
            {
                return Conflict("This set takes no votes: run metrodom serve with a jukebox policy.");
            }

            var holder = Holder(context);
            return Reply(context, desk.Vote(holder.Id, holder.Role == Role.Guest, id, up), live.Queue.Snapshot(), desk, holder.Id);
        }

        guarded.MapPost("/queue", (HttpContext context, QueueRequest? request) =>
        {
            if (library is null)
            {
                return Conflict("This set plays the files it was started with: run metrodom serve to queue songs from a library.");
            }

            if (string.IsNullOrWhiteSpace(request?.Song))
            {
                return BadRequest("song is required: a library id from GET /library.");
            }

            if (library.Find(request.Song) is not { } song)
            {
                return NotFound($"No song \"{request.Song}\" in the library: GET {Prefix}/library lists them.");
            }

            var holder = Holder(context);
            if (!live.Queue.TryAdd(song.Id, song.Title, holder.Label ?? RolePermissions.NameOf(holder.Role), request.At, out var added, out var error))
            {
                return Conflict(Capitalize(error!));
            }

            session.Messages.Add($"Queued {song.Title}.");
            return Results.Json(new { message = $"Queued {song.Title}.", song = Entry(added, live.Queue.Snapshot()) }, statusCode: StatusCodes.Status201Created);
        }).Needs(Grant.QueueEdit);
        guarded.MapPost("/queue/{id}/move", (string id, MoveRequest? request) =>
        {
            if (request?.To is not { } to)
            {
                return BadRequest("to is required: the index to move the song to.");
            }

            if (!live.Queue.Snapshot().Songs.Any(s => s.Id == id))
            {
                return NotFound($"No song {id} in the queue.");
            }

            return live.Queue.TryMove(id, to, out var error) ? Results.Json(new { message = "Moved." }) : Conflict(Capitalize(error!));
        }).Needs(Grant.QueueEdit);
        guarded.MapDelete("/queue/{id}", (string id) =>
        {
            if (live.Queue.Snapshot().Songs.FirstOrDefault(s => s.Id == id) is not { } song)
            {
                return NotFound($"No song {id} in the queue.");
            }

            return live.Queue.TryRemove(id, out var error) ? Results.Json(new { message = $"Took {song.Title} out of the queue." }) : Conflict(Capitalize(error!));
        }).Needs(Grant.QueueEdit);
        guarded.MapGet("/library", () =>
        {
            var songs = library?.Fresh() ?? [];
            return Results.Json(new
            {
                songs = songs.Select(s => new { id = s.Id, title = s.Title, durationSeconds = Math.Round(s.Duration.TotalSeconds, 1), bpm = Math.Round(s.Bpm, 1) }),
            });
        }).Needs(Grant.View);
        guarded.MapGet("/instruments", () => Results.Json(Instruments(session))).Needs(Grant.View);

        guarded.MapPost("/transport/start", () =>
        {
            if (live.IsPlaying)
            {
                return Conflict("A song is already playing.");
            }

            live.End(LiveSignal.Start);
            return Accepted("Starting.");
        }).Needs(Grant.Transport);
        guarded.MapPost("/transport/stop", () =>
        {
            if (!live.IsPlaying)
            {
                return Conflict("Nothing is playing.");
            }

            live.Control(ControlAction.StartStop);
            return Accepted("Stopping.");
        }).Needs(Grant.Transport);
        guarded.MapPost("/transport/next", () =>
        {
            live.Control(ControlAction.Next);
            return Accepted("Going to the next song.");
        }).Needs(Grant.Transport);
        guarded.MapPost("/tempo", (TempoRequest? request) =>
        {
            if (request?.Bpm is not { } bpm || !(bpm >= TapTempo.MinBpm && bpm <= TapTempo.MaxBpm))
            {
                return Results.Json(new { error = $"bpm must be {TapTempo.MinBpm:0}-{TapTempo.MaxBpm:0}." }, statusCode: StatusCodes.Status400BadRequest);
            }

            live.SetTempo(bpm, "API");
            return Accepted(string.Create(CultureInfo.InvariantCulture, $"Tempo {bpm:0.#} BPM."));
        }).Needs(Grant.Tempo);
        guarded.MapPost("/tap", () =>
        {
            live.Tap("API");
            return Accepted("Tap.");
        }).Needs(Grant.Tempo);
        guarded.MapPost("/sections/vamp", () => Navigate(live, ControlAction.Vamp)).Needs(Grant.Sections);
        guarded.MapPost("/sections/outro", () => Navigate(live, ControlAction.Outro)).Needs(Grant.Sections);
        guarded.MapPost("/curfew", (CurfewRequest? request) =>
        {
            if (!TimeOnly.TryParseExact(request?.At, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                return BadRequest("at must be a local time as HH:mm, e.g. \"23:30\".");
            }

            var stop = live.SetCurfewTonight(at);
            return Accepted(string.Create(CultureInfo.InvariantCulture, $"Tonight's curfew: {stop:HH\\:mm}. No song starts that can't finish by then."));
        }).Needs(Grant.Curfew);
        guarded.MapDelete("/curfew", () => live.LiftCurfewTonight()
            ? Accepted("Tonight's curfew lifted. The policy's curfew and end time still hold.")
            : Results.Json(new { error = "No curfew was set tonight (the policy's can't be lifted from here)." }, statusCode: StatusCodes.Status409Conflict))
            .Needs(Grant.Curfew);
        guarded.MapPost("/panic", () =>
        {
            live.Control(ControlAction.Panic);
            return Results.Json(new { message = "PANIC: everything stopped." });
        }).Needs(Grant.Panic);

        guarded.MapGet("/tokens", () =>
        {
            var guests = tokens.GuestsByDoor();
            return Results.Json(new { tokens = tokens.Issued().Select(h => Describe(h, guests.GetValueOrDefault(h.Id))) });
        }).Needs(Grant.Tokens);
        guarded.MapPost("/tokens", (HttpContext context, TokenRequest? request) =>
        {
            if (request?.Role is not ("operator" or "guest"))
            {
                return BadRequest("role must be \"operator\" or \"guest\".");
            }

            var role = request.Role == "operator" ? Role.Operator : Role.Guest;
            var join = request.Join == true;
            if (join && !policy.Roles.Allows(Role.Guest, Grant.View))
            {
                return Conflict($"Guests can't see the band under the {policy.Mode} policy, so there's nothing to join.");
            }

            var life = request.Hours is { } hours ? TimeSpan.FromHours(hours) : policy.Sessions.MaxTokenLife;
            if (!tokens.TryIssue(role, life, request.Label, policy.Sessions, out var holder, out var secret, out var error, join))
            {
                return BadRequest(error!);
            }

            if (join)
            {
                var root = PairingRoot(context, endpoint);
                var joinUrl = JoinUrl(root, secret);
                return Results.Json(new
                {
                    token = secret,
                    joinUrl,
                    joinQr = PairingQr.Svg(joinUrl),
                    screenUrl = $"{root}screen.html#join={Uri.EscapeDataString(secret)}",
                    holder = Describe(holder, 0),
                }, statusCode: StatusCodes.Status201Created);
            }

            var pairing = $"{PairingRoot(context, endpoint)}#token={Uri.EscapeDataString(secret)}";
            return Results.Json(new { token = secret, pairingUrl = pairing, pairingQr = PairingQr.Svg(pairing), holder = Describe(holder, 0) }, statusCode: StatusCodes.Status201Created);
        }).Needs(Grant.Tokens);
        guarded.MapDelete("/tokens/{id}", (string id) => tokens.Revoke(id)
            ? Results.Json(new { message = $"Token {id} revoked." })
            : Results.Json(new { error = $"No token {id} (it may have expired)." }, statusCode: StatusCodes.Status404NotFound)).Needs(Grant.Tokens);

        // Public: the join code is the credential, so these sit outside the token guard.
        var gate = new JoinGate(tokens.Time);
        api.MapPost("/join", (HttpContext context) =>
        {
            var code = PresentedToken(context.Request);
            if (tokens.Find(code) is not { Door: true } door)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { error = "That join code didn't work: it may have ended. Ask the staff for the QR code." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!policy.Roles.Allows(Role.Guest, Grant.View))
            {
                return Conflict($"Guests can't see the band under the {policy.Mode} policy.");
            }

            var address = context.Connection.RemoteIpAddress;
            if (!gate.MayJoin(address, out var retry))
            {
                context.Response.Headers.RetryAfter = Math.Max(1, Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Json(new { error = $"This phone has joined {JoinGate.PerAddress} times this hour: keep the page you have, or try again in {Math.Ceiling(retry.TotalMinutes)} min." }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (!tokens.TryJoin(door, policy.Sessions, out var guest, out var secret, out var error))
            {
                return Conflict(Capitalize(error!));
            }

            gate.Joined(address);
            return Results.Json(new { token = secret, holder = Describe(guest, 0) }, statusCode: StatusCodes.Status201Created);
        });
        api.MapGet("/join/qr", (HttpContext context) =>
        {
            var code = PresentedToken(context.Request);
            if (tokens.Find(code) is not { Door: true })
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Unauthorized();
            }

            var joinUrl = JoinUrl(PairingRoot(context, endpoint), code!);
            return Results.Json(new { joinUrl, joinQr = PairingQr.Svg(joinUrl) });
        });
    }

    /// <summary>The link a join code's QR holds: the remote, which trades the code for the phone's own guest token.</summary>
    private static string JoinUrl(string root, string code) => $"{root}#join={Uri.EscapeDataString(code)}";

    private const string HolderKey = "metrodom.holder";

    private static TokenHolder Holder(HttpContext context) => (TokenHolder)context.Items[HolderKey]!;

    /// <summary>
    /// Where a phone should open the remote: the address the owner used, unless that's this
    /// computer's loopback while the API is on the LAN, which no guest's phone could reach.
    /// </summary>
    private static string PairingRoot(HttpContext context, ApiEndpoint endpoint)
    {
        var host = context.Request.Host;
        var loopback = host.Host is "localhost" || (IPAddress.TryParse(host.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        return loopback && !endpoint.IsLoopback
            ? PairingUrl(new Uri($"http://localhost:{host.Port ?? endpoint.Port}/"), endpoint, null).ToString()
            : $"{context.Request.Scheme}://{host}/";
    }

    /// <summary>The queue as the API shows it (GET /queue, stream events). Never file paths.</summary>
    internal static object Queue(QueueSnapshot queue, RequestDesk? desk = null, string? holder = null) => new
    {
        current = queue.Current,
        editableFrom = queue.EditableFrom,
        version = queue.Version,
        songs = queue.Songs.Select(s => Entry(s, queue, holder)),
        requests = desk is null ? null : new { open = desk.RequestsOpen, votes = desk.VotesOpen },
    };

    /// <summary>
    /// One queued song as clients see it. <paramref name="holder"/> marks what that holder voted for
    /// and asked for; nobody else's votes or ids are ever shown, only counted.
    /// </summary>
    private static object Entry(QueuedSong song, QueueSnapshot queue, string? holder = null)
    {
        var index = queue.Songs.ToList().FindIndex(s => s.Id == song.Id);
        return new
        {
            index,
            id = song.Id,
            title = song.Title,
            addedBy = song.AddedBy,
            editable = index >= queue.EditableFrom,
            kind = song.Kind.ToString().ToLowerInvariant(),
            votes = song.Votes,
            voted = holder is not null && song.Voters.Contains(holder),
            mine = holder is not null && song.RequestedBy == holder,
        };
    }

    /// <summary>A desk decision as HTTP: 201 a new request, 200 a vote, 409 closed or the song can't be asked for, 429 (with Retry-After) a guest limit, 404 no such song.</summary>
    private static IResult Reply(HttpContext context, DeskReply reply, QueueSnapshot queue, RequestDesk desk, string holder)
    {
        if (reply.Accepted)
        {
            return Results.Json(
                new { message = reply.Message, countedAsVote = reply.CountedAsVote, song = Entry(reply.Song!, queue, holder) },
                statusCode: reply.CountedAsVote || reply.Song!.RequestedBy != holder ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }

        if (reply.RetryAfter is { } retry)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var status = reply.Refused switch
        {
            Refusal.TooSoon => StatusCodes.Status429TooManyRequests,
            Refusal.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict,
        };
        return Results.Json(new { error = reply.Message, open = desk.RequestsOpen }, statusCode: status);
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static object Describe(TokenHolder holder, int guests) => new
    {
        id = holder.Id,
        role = RolePermissions.NameOf(holder.Role),
        label = holder.Label,
        issuedAt = holder.Issued,
        expiresAt = holder.Expires,
        join = holder.Door,
        guests = holder.Door ? guests : (int?)null,
        via = holder.Via,
    };

    private static IResult Navigate(LiveControl live, ControlAction action) =>
        live.Navigate(action) is { } message ? Results.Json(new { message }) : Conflict("Nothing is playing.");

    /// <summary>The web remote's files: (path, embedded name, content type).</summary>
    internal static readonly (string Path, string Resource, string ContentType)[] WebFiles =
    [
        ("/", "web.index.html", "text/html; charset=utf-8"),
        ("/app.js", "web.app.js", "text/javascript; charset=utf-8"),
        ("/app.css", "web.app.css", "text/css; charset=utf-8"),
        ("/sw.js", "web.sw.js", "text/javascript; charset=utf-8"),
        ("/screen.html", "web.screen.html", "text/html; charset=utf-8"),
        ("/screen.js", "web.screen.js", "text/javascript; charset=utf-8"),
        ("/screen.css", "web.screen.css", "text/css; charset=utf-8"),
        ("/manifest.webmanifest", "web.manifest.webmanifest", "application/manifest+json"),
        ("/icon.svg", "web.icon.svg", "image/svg+xml"),
        ("/icon-192.png", "web.icon-192.png", "image/png"),
        ("/icon-512.png", "web.icon-512.png", "image/png"),
    ];

    /// <summary>
    /// Locks the page down: only its own files and this server's API (HTTP and WebSocket), no
    /// inline script, no framing, no referrer (the token lives in a fragment and local storage).
    /// Its manifest and service worker are its own files too; <c>blob:</c> images are the invite
    /// QR codes the page draws from the API's reply.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' blob:; " +
        "manifest-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>Serves the web remote at <c>/</c>. Public: it holds no secrets, and every call it makes needs the token.</summary>
    private static void MapWebApp(WebApplication app)
    {
        foreach (var (path, resource, contentType) in WebFiles)
        {
            var bytes = ResourceBytes(resource);
            app.MapGet(path, (HttpContext context) =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers.CacheControl = "no-cache";
                return Results.Bytes(bytes, contentType);
            });
        }
    }

    /// <summary>
    /// The link to open on a phone: the web remote with the token in the fragment, which browsers
    /// never send to a server. Null token: the link without it (the person types it in).
    /// </summary>
    internal static Uri PairingUrl(Uri apiAddress, ApiEndpoint endpoint, string? token)
    {
        var host = endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)
            ? LanAddress() ?? "127.0.0.1"
            : endpoint.Address.ToUriHost();
        var root = $"http://{host}:{apiAddress.Port}/";
        return new Uri(token is null ? root : $"{root}#token={Uri.EscapeDataString(token)}");
    }

    /// <summary>This computer's first LAN IPv4 address, for links phones can open.</summary>
    private static string? LanAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            ?.ToString();

    /// <summary>The instruments as the API shows them (GET /instruments, stream events).</summary>
    internal static object[] Instruments(Session session)
    {
        var checks = session.LatencyChecks;
        return [.. session.Outputs.Select(o => new
        {
            name = o.Port.Name,
            channels = o.Channels.Select(c => c + 1).ToArray(),
            latencyMs = o.Profile.MaxLatency.TotalMilliseconds,
            notes = o.Profile.Notes?.Order().ToArray(),
            connected = o.IsConnected,
            latencyCheck = checks.GetValueOrDefault(o.Port.Name) is { } check
                ? new
                {
                    status = check.Status.ToString().ToLowerInvariant(),
                    worstDriftMs = check.Worst is { } worst ? Math.Round(worst.Delta.TotalMilliseconds, 1) : (double?)null,
                    silentNotes = check.Silent,
                }
                : null,
        })];
    }

    /// <summary>The state as the API shows it (GET /state, stream events).</summary>
    internal static object State(LiveState s) => new
    {
        state = s.Playing ? "playing" : "waiting",
        song = s.Index >= 0 && s.Index < s.Setlist.Count ? new { index = s.Index, title = s.Setlist[s.Index] } : null,
        positionSeconds = s.Position?.TotalSeconds,
        durationSeconds = s.Duration?.TotalSeconds,
        tempo = new { bpm = s.Bpm, speed = s.Speed, pendingBpm = s.PendingBpm, source = s.TempoSource },
        section = s.Section,
        following = s.Following is { } f ? new { state = f.State.ToString().ToLowerInvariant(), bpm = f.Bpm } : null,
        curfew = s.Curfew,
    };

    /// <summary>The token from <c>Authorization: Bearer</c>, or (browser WebSockets can't set headers) a <c>token.&lt;token&gt;</c> subprotocol.</summary>
    private static string? PresentedToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return header[scheme.Length..].Trim();
        }

        return request.Headers.SecWebSocketProtocol
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.StartsWith(StateStream.TokenPrefix, StringComparison.Ordinal))
            .Select(p => p[StateStream.TokenPrefix.Length..])
            .FirstOrDefault();
    }

    private static IResult Unauthorized() => Results.Json(
        new { error = "Missing or wrong token: send Authorization: Bearer <token shown by the Conductor>." },
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Accepted(string message) => Results.Json(new { message }, statusCode: StatusCodes.Status202Accepted);

    private static IResult Conflict(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status404NotFound);

    private sealed record QueueRequest(string? Song, int? At);

    private sealed record MoveRequest(int? To);

    private sealed record OpenRequest(bool? Open);

    private sealed record TempoRequest(double? Bpm);

    private sealed record CurfewRequest(string? At);

    private sealed record TokenRequest(string? Role, double? Hours, string? Label, bool? Join = null);

    /// <summary>A host lifetime that ignores process signals.</summary>
    private sealed class NoSignals : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal static class RouteGrants
{
    /// <summary>Declares what a route needs (see <see cref="Requires"/>).</summary>
    public static RouteHandlerBuilder Needs(this RouteHandlerBuilder route, Grant? grant) => route.WithMetadata(new Requires(grant));
}

internal static class IPAddressExtensions
{
    public static string ToUriHost(this IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
