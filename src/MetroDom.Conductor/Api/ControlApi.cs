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

/// <summary>
/// The Control API v0 (ADR 0003): HTTP + JSON at <c>/api/v0</c> on top of a running set's
/// <see cref="LiveControl"/>. Every route but the spec needs the bearer token. Routes and the
/// checked-in <c>openapi.json</c> are kept equal by a test. See docs/references/control-api.md.
/// </summary>
internal sealed class ControlApi : IAsyncDisposable
{
    public const string Prefix = "/api/v0";

    /// <summary>Requests are tiny intents; anything bigger is a mistake or an attack.</summary>
    private const long MaxBodyBytes = 4096;

    private readonly WebApplication _app;

    private ControlApi(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>Where it actually listens (the real port when started on port 0).</summary>
    public Uri Address { get; }

    /// <summary>The checked-in spec, embedded in the binary.</summary>
    public static string Spec()
    {
        using var stream = typeof(ControlApi).Assembly.GetManifestResourceStream("MetroDom.Conductor.Api.openapi.json")
            ?? throw new InvalidOperationException("openapi.json is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>A random token: 128 bits, URL-safe.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Starts listening. Throws if the port is taken.</summary>
    public static ControlApi Start(ApiEndpoint endpoint, string token, LiveControl live, Session session)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
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
        Map(app, token, live, session);
        app.StartAsync().GetAwaiter().GetResult();

        var bound = app.Urls.Select(u => new Uri(u)).First();
        return new ControlApi(app, new Uri($"http://{endpoint.Address.ToUriHost()}:{bound.Port}{Prefix}/"));
    }

    /// <summary>The routes this server answers.</summary>
    internal IEnumerable<(string Method, string Path)> Routes() => Routes(_app);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The routes (method, path under <see cref="Prefix"/>) the server answers, for the spec test.</summary>
    internal static IEnumerable<(string Method, string Path)> Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (m, "/" + e.RoutePattern.RawText!.TrimStart('/'))))
            .Select(r => (r.m, r.Item2[Prefix.Length..]));

    internal static void Map(WebApplication app, string token, LiveControl live, Session session)
    {
        var expected = Encoding.UTF8.GetBytes(token);
        var spec = Spec();
        var api = app.MapGroup(Prefix);
        api.MapGet("/openapi.json", () => Results.Text(spec, "application/json"));

        var guarded = api.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            if (Authorized(context.HttpContext.Request, expected))
            {
                return await next(context).ConfigureAwait(false);
            }

            context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
            return Unauthorized();
        });

        guarded.MapGet("/state", () => Results.Json(State(live.Snapshot())));
        guarded.MapGet("/queue", () =>
        {
            var state = live.Snapshot();
            return Results.Json(new { current = state.Index, songs = state.Setlist.Select((title, index) => new { index, title }) });
        });
        guarded.MapGet("/instruments", () => Results.Json(session.Outputs.Select(o => new
        {
            name = o.Port.Name,
            channels = o.Channels.Select(c => c + 1),
            latencyMs = o.Profile.MaxLatency.TotalMilliseconds,
            notes = o.Profile.Notes?.Order(),
            connected = o.IsConnected,
        })));

        guarded.MapPost("/transport/start", () =>
        {
            if (live.IsPlaying)
            {
                return Conflict("A song is already playing.");
            }

            live.End(LiveSignal.Start);
            return Accepted("Starting.");
        });
        guarded.MapPost("/transport/stop", () =>
        {
            if (!live.IsPlaying)
            {
                return Conflict("Nothing is playing.");
            }

            live.Control(ControlAction.StartStop);
            return Accepted("Stopping.");
        });
        guarded.MapPost("/transport/next", () =>
        {
            live.Control(ControlAction.Next);
            return Accepted("Going to the next song.");
        });
        guarded.MapPost("/tempo", (TempoRequest? request) =>
        {
            if (request?.Bpm is not { } bpm || !(bpm >= TapTempo.MinBpm && bpm <= TapTempo.MaxBpm))
            {
                return Results.Json(new { error = $"bpm must be {TapTempo.MinBpm:0}-{TapTempo.MaxBpm:0}." }, statusCode: StatusCodes.Status400BadRequest);
            }

            live.SetTempo(bpm, "API");
            return Accepted(string.Create(CultureInfo.InvariantCulture, $"Tempo {bpm:0.#} BPM."));
        });
        guarded.MapPost("/tap", () =>
        {
            live.Tap("API");
            return Accepted("Tap.");
        });
        guarded.MapPost("/sections/vamp", () => Navigate(live, ControlAction.Vamp));
        guarded.MapPost("/sections/outro", () => Navigate(live, ControlAction.Outro));
        guarded.MapPost("/panic", () =>
        {
            live.Control(ControlAction.Panic);
            return Results.Json(new { message = "PANIC: everything stopped." });
        });
    }

    private static IResult Navigate(LiveControl live, ControlAction action) =>
        live.Navigate(action) is { } message ? Results.Json(new { message }) : Conflict("Nothing is playing.");

    private static object State(LiveState s) => new
    {
        state = s.Playing ? "playing" : "waiting",
        song = s.Index >= 0 && s.Index < s.Setlist.Count ? new { index = s.Index, title = s.Setlist[s.Index] } : null,
        positionSeconds = s.Position?.TotalSeconds,
        durationSeconds = s.Duration?.TotalSeconds,
        tempo = new { bpm = s.Bpm, speed = s.Speed, pendingBpm = s.PendingBpm, source = s.TempoSource },
        section = s.Section,
        following = s.Following is { } f ? new { state = f.State.ToString().ToLowerInvariant(), bpm = f.Bpm } : null,
    };

    private static bool Authorized(HttpRequest request, byte[] expected)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static IResult Unauthorized() => Results.Json(
        new { error = "Missing or wrong token: send Authorization: Bearer <token shown by the Conductor>." },
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Accepted(string message) => Results.Json(new { message }, statusCode: StatusCodes.Status202Accepted);

    private static IResult Conflict(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);

    private sealed record TempoRequest(double? Bpm);

    /// <summary>A host lifetime that ignores process signals.</summary>
    private sealed class NoSignals : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal static class IPAddressExtensions
{
    public static string ToUriHost(this IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
