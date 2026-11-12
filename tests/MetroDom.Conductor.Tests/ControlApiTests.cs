using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Api;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

public sealed class ControlApiTests : IAsyncDisposable
{
    private const string Token = "test-token-123";
    private readonly VirtualClock _clock = new();
    private readonly Port _port = new();
    private readonly Session _session;
    private readonly LiveControl _live;
    private readonly ControlApi _api;
    private readonly HttpClient _http;

    public ControlApiTests()
    {
        _session = new Session([new SessionOutput(_port, [0, 9], TimeSpan.FromMilliseconds(12), new HashSet<int> { 64, 60 })], []);
        _live = new LiveControl(_session, _clock);
        _live.UseSetlist(["one.mid", "two.mid"]);
        _live.Cue(0);
        _api = ControlApi.Start(new ApiEndpoint(IPAddress.Loopback, 0), Token, _live, _session);
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

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string? json = null, string? token = Token)
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
        using var response = await _http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, string? token = Token) => Send(HttpMethod.Get, path, token: token);

    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, string? json = null) => Send(HttpMethod.Post, path, json);

    private LatencyScheduler Playing()
    {
        var scheduler = new LatencyScheduler(_session.Orchestra, _clock, _session.Profiles);
        scheduler.Start(Enumerable.Range(0, 8).Select(i => new TimedMidiEvent(TimeSpan.FromMilliseconds(500 * i), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100))));
        _live.Begin(new ActiveSong(scheduler, new SectionNavigator(scheduler, []), (bpm, _) => scheduler.SetSpeed(bpm / 120), () => { }, () => 120));
        return scheduler;
    }

    [Fact]
    public void Routes_and_the_checked_in_spec_match_exactly()
    {
        using var spec = JsonDocument.Parse(ControlApi.Spec());
        var documented = spec.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(m => (m.Name.ToUpperInvariant(), p.Name)))
            .Order().ToArray();
        var served = _api.Routes().Order().ToArray();
        Assert.Equal(documented, served);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("test-token-12")]
    public async Task Every_route_but_the_spec_needs_the_token(string? token)
    {
        var (status, body) = await Get("state", token);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("token", body.GetProperty("error").GetString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await Get("openapi.json", token)).Status);
        Assert.False(_live.Done.IsSet);
    }

    [Fact]
    public async Task Wrong_token_cannot_panic_or_start()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "panic", token: "nope")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "transport/start", token: null)).Status);
        Assert.False(_live.Done.IsSet);
    }

    [Fact]
    public async Task State_queue_and_instruments_describe_the_band()
    {
        var (_, state) = await Get("state");
        Assert.Equal("waiting", state.GetProperty("state").GetString());
        Assert.Equal("one.mid", state.GetProperty("song").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("positionSeconds").ValueKind);

        var (_, queue) = await Get("queue");
        Assert.Equal(0, queue.GetProperty("current").GetInt32());
        Assert.Equal(["one.mid", "two.mid"], queue.GetProperty("songs").EnumerateArray().Select(s => s.GetProperty("title").GetString()));

        var (_, instruments) = await Get("instruments");
        var band = instruments.EnumerateArray().Single();
        Assert.Equal("band", band.GetProperty("name").GetString());
        Assert.Equal([1, 10], band.GetProperty("channels").EnumerateArray().Select(c => c.GetInt32()));
        Assert.Equal(12, band.GetProperty("latencyMs").GetDouble());
        Assert.Equal([60, 64], band.GetProperty("notes").EnumerateArray().Select(n => n.GetInt32()));
        Assert.True(band.GetProperty("connected").GetBoolean());

        Playing();
        _clock.Advance(TimeSpan.FromSeconds(1));
        (_, state) = await Get("state");
        Assert.Equal("playing", state.GetProperty("state").GetString());
        Assert.Equal(1 - 0.012, state.GetProperty("positionSeconds").GetDouble(), 3);
        Assert.Equal(120, state.GetProperty("tempo").GetProperty("bpm").GetDouble());
    }

    [Fact]
    public async Task Start_and_stop_only_where_they_make_sense()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Post("transport/stop")).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await Post("transport/start")).Status);
        Assert.Equal(LiveSignal.Start, _live.Signal);

        _live.Begin(null);
        Playing();
        Assert.Equal(HttpStatusCode.Conflict, (await Post("transport/start")).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await Post("transport/stop")).Status);
        Assert.Equal(LiveSignal.Stopped, _live.Signal);
    }

    [Fact]
    public async Task Next_goes_to_the_next_song()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Post("transport/next")).Status);
        Assert.Equal(LiveSignal.Next, _live.Signal);
    }

    [Theory]
    [InlineData("{\"bpm\": 10}")]
    [InlineData("{\"bpm\": 301}")]
    [InlineData("{}")]
    [InlineData("{\"bpm\": \"fast\"}")]
    [InlineData("not json")]
    public async Task Tempo_rejects_nonsense(string json)
    {
        var (status, _) = await Post("tempo", json);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Null(_live.TakePendingBpm());
    }

    [Fact]
    public async Task Tempo_sets_the_next_song_while_waiting_and_the_song_while_playing()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Post("tempo", "{\"bpm\": 96}")).Status);
        Assert.Equal(96, (await Get("state")).Body.GetProperty("tempo").GetProperty("pendingBpm").GetDouble());
        Assert.Equal(96, _live.TakePendingBpm());

        var scheduler = Playing();
        Assert.Equal(HttpStatusCode.Accepted, (await Post("tempo", "{\"bpm\": 90}")).Status);
        Assert.Equal(0.75, scheduler.Speed, 6);
    }

    [Fact]
    public async Task Taps_share_tap_tempo()
    {
        await Post("tap");
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await Post("tap");
        Assert.Equal(120, _live.TakePendingBpm()!.Value, 6);
    }

    [Fact]
    public async Task Sections_need_a_song_and_say_what_happens()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Post("sections/vamp")).Status);
        Playing();
        var (status, body) = await Post("sections/outro");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("No sections", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panic_silences_every_output_and_waits()
    {
        var scheduler = Playing();
        var (status, _) = await Post("panic");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(LiveSignal.Panic, _live.Signal);
        Assert.True(scheduler.IsFinished);
        Assert.Contains(_port.Sent, e => e is ControlChangeEvent c && c.ControlNumber == 123);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused()
    {
        var (status, _) = await Post("tempo", "{\"bpm\": 100, \"pad\": \"" + new string('x', 8000) + "\"}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
    }

    [Theory]
    [InlineData(null, "127.0.0.1", 7890, true)]
    [InlineData("8000", "127.0.0.1", 8000, true)]
    [InlineData("0.0.0.0:7890", "0.0.0.0", 7890, false)]
    [InlineData("[::1]:9000", "::1", 9000, true)]
    public void Endpoint_parses(string? raw, string address, int port, bool loopback)
    {
        Assert.True(ApiEndpoint.TryParse(raw, out var endpoint, out var error), error);
        Assert.Equal((IPAddress.Parse(address), port, loopback), (endpoint.Address, endpoint.Port, endpoint.IsLoopback));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("70000")]
    [InlineData("1.2.3.4")]
    [InlineData("0")]
    public void Endpoint_rejects_nonsense(string raw) => Assert.False(ApiEndpoint.TryParse(raw, out _, out _));

    [Fact]
    public void Tokens_are_random_and_url_safe()
    {
        var a = ControlApi.NewToken();
        Assert.NotEqual(a, ControlApi.NewToken());
        Assert.Matches("^[A-Za-z0-9_-]{22}$", a);
    }

    private Uri StreamUri => new(_api.Address.ToString().Replace("http://", "ws://", StringComparison.Ordinal) + "stream");

    private async Task<ClientWebSocket> Connect(Action<ClientWebSocketOptions>? configure = null)
    {
        var socket = new ClientWebSocket();
        if (configure is null)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        }
        else
        {
            configure(socket.Options);
        }

        await socket.ConnectAsync(StreamUri, Ct);
        return socket;
    }

    private static async Task<JsonElement> Next(ClientWebSocket socket, string? type = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            Assert.True(result.EndOfMessage);
            var element = JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
            if (type is null || element.GetProperty("type").GetString() == type)
            {
                return element;
            }
        }
    }

    [Fact]
    public async Task Stream_says_hello_then_state_and_instruments()
    {
        using var socket = await Connect();
        var hello = await Next(socket);
        Assert.Equal(("hello", 1, "v0"), (hello.GetProperty("type").GetString(), hello.GetProperty("seq").GetInt32(), hello.GetProperty("api").GetString()));

        var state = await Next(socket);
        Assert.Equal(("state", 2), (state.GetProperty("type").GetString(), state.GetProperty("seq").GetInt32()));
        Assert.Equal("waiting", state.GetProperty("state").GetProperty("state").GetString());

        var instruments = await Next(socket);
        Assert.Equal("instruments", instruments.GetProperty("type").GetString());
        Assert.Equal("band", instruments.GetProperty("instruments")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Stream_sends_changes_positions_and_messages()
    {
        using var socket = await Connect();
        await Next(socket, "instruments");

        Playing();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var state = await Next(socket, "state");
        Assert.Equal("playing", state.GetProperty("state").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("state").GetProperty("positionSeconds").ValueKind); // Position has its own events.

        var position = await Next(socket, "position");
        Assert.Equal(1 - 0.012, position.GetProperty("positionSeconds").GetDouble(), 3);
        Assert.Equal(3.5, position.GetProperty("durationSeconds").GetDouble(), 3);

        _session.Messages.Add("Vamp on: repeating Chorus until released.");
        var message = await Next(socket, "message");
        Assert.Equal("Vamp on: repeating Chorus until released.", message.GetProperty("text").GetString());

        var seqs = new[] { state, position, message }.Select(e => e.GetProperty("seq").GetInt64()).ToArray();
        Assert.True(seqs[0] < seqs[1] && seqs[1] < seqs[2]);
    }

    [Fact]
    public async Task Stream_accepts_the_token_as_a_subprotocol_for_browsers()
    {
        using var socket = await Connect(o =>
        {
            o.AddSubProtocol(StateStream.Subprotocol);
            o.AddSubProtocol(StateStream.TokenPrefix + Token);
        });
        Assert.Equal(StateStream.Subprotocol, socket.SubProtocol);
        Assert.Equal("hello", (await Next(socket)).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("token.wrong")]
    public async Task Stream_refuses_without_the_token(string? subprotocol)
    {
        await Assert.ThrowsAsync<WebSocketException>(() => Connect(o =>
        {
            o.AddSubProtocol(StateStream.Subprotocol);
            if (subprotocol is not null)
            {
                o.AddSubProtocol(subprotocol);
            }
        }));
    }

    [Fact]
    public async Task Stream_is_a_websocket_only()
    {
        var (status, body) = await Get("stream");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("WebSocket", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_count_is_capped_and_freed_on_close()
    {
        var open = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < StateStream.MaxStreams; i++)
            {
                open.Add(await Connect());
            }

            await Assert.ThrowsAsync<WebSocketException>(() => Connect());

            await open[0].CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
            open[0].Dispose();
            open.RemoveAt(0);
            await Task.Delay(300, Ct); // The server notices the close on its next read.
            open.Add(await Connect());
        }
        finally
        {
            foreach (var socket in open)
            {
                socket.Dispose();
            }
        }
    }

    [Fact]
    public async Task Stopping_the_api_closes_open_streams_quickly()
    {
        using var socket = await Connect();
        await Next(socket, "instruments");

        // Keep reading, as a real client does, so the client answers the server's close. A client
        // that isn't reading never answers; the server then resets the connection, and Windows
        // drops the unread close frame with it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var closed = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
            }
            while (result.MessageType != WebSocketMessageType.Close);
            return result.CloseStatus;
        });

        var started = DateTime.UtcNow;
        await _api.DisposeAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await closed);
    }

    [Fact]
    public void Event_schema_is_served_and_names_every_event()
    {
        using var schema = JsonDocument.Parse(ControlApi.EventSchema());
        var types = schema.RootElement.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(t => t.GetString());
        Assert.Equal(["hello", "state", "position", "instruments", "message"], types);
    }

    [Theory]
    [InlineData("/", "text/html", "<script src=\"app.js\" defer>")]
    [InlineData("/app.js", "text/javascript", "api/v0/")]
    [InlineData("/app.css", "text/css", "--panic")]
    public async Task Web_remote_is_served_without_a_token_and_locked_down(string path, string contentType, string contains)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(_api.Address, path), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        Assert.Contains(contains, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public void Web_remote_has_no_inline_script_or_style_and_no_secrets()
    {
        var html = ControlApi.WebFiles.Select(f => f.Resource).First(r => r.EndsWith(".html", StringComparison.Ordinal));
        using var stream = typeof(ControlApi).Assembly.GetManifestResourceStream("MetroDom.Conductor.Api." + html)!;
        var page = new StreamReader(stream).ReadToEnd();
        Assert.DoesNotMatch("<script>|<script [^>]*>[^<]+</script>|<style|\\sstyle=|\\son[a-z]+=", page);

        using var js = typeof(ControlApi).Assembly.GetManifestResourceStream("MetroDom.Conductor.Api.web.app.js")!;
        var script = new StreamReader(js).ReadToEnd();
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal); // Titles and messages go in as text.
        Assert.DoesNotContain("eval(", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Pairing_url_carries_the_token_in_the_fragment_only()
    {
        var address = new Uri("http://127.0.0.1:7890/api/v0/");
        var url = ControlApi.PairingUrl(address, new ApiEndpoint(IPAddress.Loopback, 7890), "a+b/c");
        Assert.Equal("http://127.0.0.1:7890/", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("#token=a%2Bb%2Fc", url.Fragment);
        Assert.Empty(url.Query);

        Assert.Equal("", ControlApi.PairingUrl(address, new ApiEndpoint(IPAddress.Loopback, 7890), null).Fragment);
        var lan = ControlApi.PairingUrl(address, new ApiEndpoint(IPAddress.Any, 7890), "t");
        Assert.NotEqual("0.0.0.0", lan.Host); // A phone can't open 0.0.0.0.
    }

    private sealed class Port : IMidiOutputPort
    {
        public string Name => "band";

        public ConcurrentBag<MidiEvent> Sent { get; } = [];

        public void Send(MidiEvent midiEvent) => Sent.Add(midiEvent);

        public void Dispose()
        {
        }
    }
}
