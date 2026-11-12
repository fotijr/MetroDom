using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Api;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Policy;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

/// <summary>Roles and tokens (ADR 0005, docs/SECURITY.md): the owner hands out expiring tokens; the policy says what each role may do.</summary>
public sealed class ControlApiRolesTests : IAsyncDisposable
{
    private const string Owner = "owner-token-123";
    private readonly ManualTime _time = new();
    private readonly Session _session;
    private readonly LiveControl _live;
    private readonly HttpClient _http;
    private ControlApi _api;

    public ControlApiRolesTests()
    {
        _session = new Session([new SessionOutput(new NullPort(), [0], TimeSpan.Zero)], []);
        _live = new LiveControl(_session, new VirtualClock());
        _live.UseSetlist(["one.mid"]);
        _live.Cue(0);
        _api = Start(Policy("crowd-jukebox"));
        _http = new HttpClient { BaseAddress = _api.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _api.DisposeAsync();
        _live.Dispose();
        _session.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PolicyProfile Policy(string mode)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "policies", mode + ".json");
            if (File.Exists(path))
            {
                Assert.True(PolicyProfile.TryParse(File.ReadAllText(path), out var policy, out var error), error);
                return policy;
            }
        }

        throw new FileNotFoundException(mode);
    }

    private ControlApi Start(PolicyProfile policy) =>
        ControlApi.Start(new ApiEndpoint(IPAddress.Loopback, 0), new TokenStore(Owner, _time), policy, _live, _session);

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string? token, string? json = null, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await (http ?? _http).SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(string Token, string Id)> Issue(string role, double? hours = null, string? label = null)
    {
        var body = JsonSerializer.Serialize(new { role, hours, label });
        var (status, reply) = await Send(HttpMethod.Post, "tokens", Owner, body);
        Assert.Equal(HttpStatusCode.Created, status);
        return (reply.GetProperty("token").GetString()!, reply.GetProperty("holder").GetProperty("id").GetString()!);
    }

    [Fact]
    public void Every_route_declares_the_grant_the_spec_says()
    {
        using var spec = JsonDocument.Parse(ControlApi.Spec());
        var documented = spec.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject()
                .Where(m => !(m.Value.TryGetProperty("security", out var s) && s.GetArrayLength() == 0))
                .Select(m => (m.Name.ToUpperInvariant(), p.Name, m.Value.TryGetProperty("x-metrodom-grant", out var g) ? g.GetString() : "")))
            .Order().ToArray();
        var served = _api.Grants().Where(g => g.Path is not ("/openapi.json" or "/events.schema.json")).Order().ToArray();

        Assert.DoesNotContain(served, r => r.Grant is null); // a route without a grant is refused to everyone
        Assert.Equal(documented, served);
    }

    [Fact]
    public async Task Owner_token_is_the_owner_and_can_do_everything()
    {
        var (status, session) = await Send(HttpMethod.Get, "session", Owner);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("owner", session.GetProperty("role").GetString());
        Assert.Equal("crowd-jukebox", session.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("expiresAt").ValueKind);
        Assert.Contains("tokens", session.GetProperty("grants").EnumerateArray().Select(g => g.GetString()));
    }

    [Fact]
    public async Task Guest_may_view_but_never_drive_or_panic()
    {
        var (guest, _) = await Issue("guest", label: "table 4");

        var (_, session) = await Send(HttpMethod.Get, "session", guest);
        Assert.Equal("guest", session.GetProperty("role").GetString());
        Assert.Equal("table 4", session.GetProperty("label").GetString());
        Assert.Equal(["view", "queue.request", "queue.vote"], session.GetProperty("grants").EnumerateArray().Select(g => g.GetString()!));

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "state", guest)).Status);
        foreach (var path in new[] { "panic", "transport/start", "tap", "sections/vamp" })
        {
            var (status, body) = await Send(HttpMethod.Post, path, guest);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Contains("A guest can't", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        Assert.False(_live.IsPlaying);
    }

    [Fact]
    public async Task Operator_runs_the_show_but_cannot_hand_out_tokens()
    {
        var (op, _) = await Issue("operator", hours: 2);

        Assert.Equal(HttpStatusCode.Accepted, (await Send(HttpMethod.Post, "tap", op)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "panic", op)).Status);
        var (status, body) = await Send(HttpMethod.Post, "tokens", op, """{"role":"guest"}""");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("An operator can't", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "tokens", op)).Status);
    }

    [Fact]
    public async Task Live_band_guests_get_nothing_but_their_session()
    {
        await using var api = Start(PolicyProfile.LiveBand);
        using var http = new HttpClient { BaseAddress = api.Address };
        var (_, reply) = await Send(HttpMethod.Post, "tokens", Owner, """{"role":"guest"}""", http);
        var guest = reply.GetProperty("token").GetString();

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "session", guest, http: http)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "state", guest, http: http)).Status);
    }

    [Fact]
    public async Task Tokens_end_when_they_expire_or_are_revoked()
    {
        var (shortLived, _) = await Issue("operator", hours: 1);
        var (revoked, id) = await Issue("guest");

        var (_, list) = await Send(HttpMethod.Get, "tokens", Owner);
        Assert.Equal(2, list.GetProperty("tokens").GetArrayLength());
        Assert.DoesNotContain(shortLived, list.GetRawText(), StringComparison.Ordinal); // secrets are never listed

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"tokens/{id}", Owner)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "session", revoked)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"tokens/{id}", Owner)).Status);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "session", shortLived)).Status);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "session", shortLived)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "session", Owner)).Status); // the owner's lasts the run
    }

    [Theory]
    [InlineData("""{"role":"owner"}""", "operator\" or \"guest")]
    [InlineData("""{"role":"admin"}""", "operator\" or \"guest")]
    [InlineData("""{}""", "operator\" or \"guest")]
    [InlineData("""{"role":"guest","hours":13}""", "at most 12 hours")] // crowd-jukebox: maxTokenHours 12
    [InlineData("""{"role":"guest","hours":0}""", "more than 0")]
    [InlineData("""{"role":"guest","label":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""", "at most 60")]
    public async Task Issue_refuses_what_the_policy_does_not_allow(string json, string reason)
    {
        var (status, body) = await Send(HttpMethod.Post, "tokens", Owner, json);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(reason, body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Issued_token_comes_with_a_pairing_link_in_the_fragment()
    {
        var (_, reply) = await Send(HttpMethod.Post, "tokens", Owner, """{"role":"guest","hours":3}""");
        var url = new Uri(reply.GetProperty("pairingUrl").GetString()!);
        Assert.Equal($"#token={reply.GetProperty("token").GetString()}", url.Fragment);
        Assert.Equal(_api.Address.Port, url.Port);
        var holder = reply.GetProperty("holder");
        Assert.Equal(TimeSpan.FromHours(3), holder.GetProperty("expiresAt").GetDateTimeOffset() - holder.GetProperty("issuedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Revoking_a_token_closes_its_open_stream()
    {
        var (guest, id) = await Issue("guest");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {guest}");
        var stream = new UriBuilder(new Uri(_api.Address, "stream")) { Scheme = "ws" }.Uri;
        await socket.ConnectAsync(stream, Ct);

        await Send(HttpMethod.Delete, $"tokens/{id}", Owner);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
        }
        while (result.MessageType != WebSocketMessageType.Close);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NullPort : IMidiOutputPort
    {
        public string Name => "band";

        public void Send(MidiEvent midiEvent)
        {
        }

        public void Dispose()
        {
        }
    }
}
