using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MetroDom.Conductor.Commands;
using MetroDom.Core.Policy;
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

    /// <summary>Starts listening. Throws if the port is taken.</summary>
    public static ControlApi Start(ApiEndpoint endpoint, TokenStore tokens, PolicyProfile policy, LiveControl live, Session session)
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
        Map(app, tokens, policy, live, session);
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

    internal static void Map(WebApplication app, TokenStore tokens, PolicyProfile policy, LiveControl live, Session session)
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
                grants = policy.Roles.For(holder.Role).Select(RolePermissions.NameOf),
            });
        }).Needs(null);
        guarded.MapGet("/state", () => Results.Json(State(live.Snapshot()))).Needs(Grant.View);
        var stream = new StateStream(live, session);
        guarded.MapGet("/stream", (Delegate)((HttpContext context) =>
        {
            var holder = Holder(context);
            return stream.Accept(context, () => tokens.IsValid(holder), app.Lifetime.ApplicationStopping);
        })).Needs(Grant.View);
        guarded.MapGet("/queue", () =>
        {
            var state = live.Snapshot();
            return Results.Json(new { current = state.Index, songs = state.Setlist.Select((title, index) => new { index, title }) });
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
        guarded.MapPost("/panic", () =>
        {
            live.Control(ControlAction.Panic);
            return Results.Json(new { message = "PANIC: everything stopped." });
        }).Needs(Grant.Panic);

        guarded.MapGet("/tokens", () => Results.Json(new { tokens = tokens.Issued().Select(Describe) })).Needs(Grant.Tokens);
        guarded.MapPost("/tokens", (HttpContext context, TokenRequest? request) =>
        {
            if (request?.Role is not ("operator" or "guest"))
            {
                return BadRequest("role must be \"operator\" or \"guest\".");
            }

            var role = request.Role == "operator" ? Role.Operator : Role.Guest;
            var life = request.Hours is { } hours ? TimeSpan.FromHours(hours) : policy.Sessions.MaxTokenLife;
            if (!tokens.TryIssue(role, life, request.Label, policy.Sessions, out var holder, out var secret, out var error))
            {
                return BadRequest(error!);
            }

            var pairing = $"{context.Request.Scheme}://{context.Request.Host}/#token={Uri.EscapeDataString(secret)}";
            return Results.Json(new { token = secret, pairingUrl = pairing, holder = Describe(holder) }, statusCode: StatusCodes.Status201Created);
        }).Needs(Grant.Tokens);
        guarded.MapDelete("/tokens/{id}", (string id) => tokens.Revoke(id)
            ? Results.Json(new { message = $"Token {id} revoked." })
            : Results.Json(new { error = $"No token {id} (it may have expired)." }, statusCode: StatusCodes.Status404NotFound)).Needs(Grant.Tokens);
    }

    private const string HolderKey = "metrodom.holder";

    private static TokenHolder Holder(HttpContext context) => (TokenHolder)context.Items[HolderKey]!;

    private static object Describe(TokenHolder holder) => new
    {
        id = holder.Id,
        role = RolePermissions.NameOf(holder.Role),
        label = holder.Label,
        issuedAt = holder.Issued,
        expiresAt = holder.Expires,
    };

    private static IResult Navigate(LiveControl live, ControlAction action) =>
        live.Navigate(action) is { } message ? Results.Json(new { message }) : Conflict("Nothing is playing.");

    /// <summary>The web remote's files: (path, embedded name, content type).</summary>
    internal static readonly (string Path, string Resource, string ContentType)[] WebFiles =
    [
        ("/", "web.index.html", "text/html; charset=utf-8"),
        ("/app.js", "web.app.js", "text/javascript; charset=utf-8"),
        ("/app.css", "web.app.css", "text/css; charset=utf-8"),
    ];

    /// <summary>
    /// Locks the page down: only its own files and this server's API (HTTP and WebSocket), no
    /// inline script, no framing, no referrer (the token lives in a fragment and local storage).
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>Serves the web remote at <c>/</c>. Public: it holds no secrets, and every call it makes needs the token.</summary>
    private static void MapWebApp(WebApplication app)
    {
        foreach (var (path, resource, contentType) in WebFiles)
        {
            var bytes = Encoding.UTF8.GetBytes(Resource(resource));
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
    internal static object[] Instruments(Session session) => [.. session.Outputs.Select(o => new
    {
        name = o.Port.Name,
        channels = o.Channels.Select(c => c + 1).ToArray(),
        latencyMs = o.Profile.MaxLatency.TotalMilliseconds,
        notes = o.Profile.Notes?.Order().ToArray(),
        connected = o.IsConnected,
    })];

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

    private sealed record TempoRequest(double? Bpm);

    private sealed record TokenRequest(string? Role, double? Hours, string? Label);

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
