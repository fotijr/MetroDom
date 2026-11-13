using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Api;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Conductor.Service;
using MetroDom.Core.Policy;
using MetroDom.Core.Setlist;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

/// <summary>The editable queue and the library over the Control API, as <c>serve</c> runs them (plan 0004 S3).</summary>
public sealed class QueueApiTests : IAsyncDisposable
{
    private const string Owner = "owner-token-123";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"metrodom-queue-{Guid.NewGuid():N}");
    private readonly Session _session;
    private readonly LiveControl _live;
    private readonly TokenStore _tokens = new(Owner);
    private readonly ControlApi _api;
    private readonly HttpClient _http;

    public QueueApiTests()
    {
        foreach (var name in new[] { "Opener.mid", "Ballad.mid", "covers/Closer.mid" })
        {
            var path = Path.Combine(_dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DemoSong.Create().Write(path);
        }

        _session = new Session([new SessionOutput(new NullPort(), [0], TimeSpan.Zero)], []);
        _live = new LiveControl(_session, new VirtualClock());
        var policy = Policy("crowd-jukebox");
        _api = ControlApi.Start(new ApiEndpoint(IPAddress.Loopback, 0), _tokens, policy, _live, _session, new SongLibrary(_dir), new RequestDesk(_live.Queue, policy.Queue, new SystemClock()));
        _http = new HttpClient { BaseAddress = _api.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _api.DisposeAsync();
        _live.Dispose();
        _session.Dispose();
        Directory.Delete(_dir, recursive: true);
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

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string? json = null, string token = Owner, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await (http ?? _http).SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> Token(Role role, string label)
    {
        Assert.True(_tokens.TryIssue(role, TimeSpan.FromHours(1), label, SessionRules.Default, out _, out var secret, out _));
        return await Task.FromResult(secret);
    }

    private async Task<string[]> Titles()
    {
        var (_, queue) = await Send(HttpMethod.Get, "queue");
        return [.. queue.GetProperty("songs").EnumerateArray().Select(s => s.GetProperty("title").GetString()!)];
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Queue(string song, int? at = null, string token = Owner) =>
        Send(HttpMethod.Post, "queue", JsonSerializer.Serialize(new { song, at }), token);

    [Fact]
    public async Task Library_lists_songs_by_title_without_paths()
    {
        var (status, library) = await Send(HttpMethod.Get, "library");
        Assert.Equal(HttpStatusCode.OK, status);
        var songs = library.GetProperty("songs").EnumerateArray().ToArray();
        Assert.Equal(["Ballad", "Closer", "Opener"], songs.Select(s => s.GetProperty("title").GetString()));
        Assert.Equal("covers/Closer.mid", songs[1].GetProperty("id").GetString());
        Assert.Equal(100, songs[0].GetProperty("bpm").GetDouble());
        Assert.DoesNotContain(_dir, library.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queueing_says_who_added_it()
    {
        var op = await Token(Role.Operator, "Sam");
        var (status, reply) = await Queue("Opener.mid", token: op);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Sam", reply.GetProperty("song").GetProperty("addedBy").GetString());
        await Queue("covers/Closer.mid");

        var (_, queue) = await Send(HttpMethod.Get, "queue");
        Assert.Equal(["Opener", "Closer"], await Titles());
        Assert.Equal("owner", queue.GetProperty("songs")[1].GetProperty("addedBy").GetString());
        Assert.DoesNotContain(_dir, queue.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coming_songs_move_and_go_but_the_one_up_now_stays()
    {
        await Queue("Opener.mid");
        await Queue("Ballad.mid");
        await Queue("covers/Closer.mid");
        _live.Cue(0); // Opener is up (waiting for start or playing)

        var (_, queue) = await Send(HttpMethod.Get, "queue");
        var ids = queue.GetProperty("songs").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(1, queue.GetProperty("editableFrom").GetInt32());
        Assert.False(queue.GetProperty("songs")[0].GetProperty("editable").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"queue/{ids[2]}/move", """{"to":1}""")).Status);
        Assert.Equal(["Opener", "Closer", "Ballad"], await Titles());

        var (status, body) = await Send(HttpMethod.Delete, $"queue/{ids[0]}");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("skip it with next", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"queue/{ids[1]}/move", """{"to":0}""")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Queue("Ballad.mid", at: 0)).Status);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"queue/{ids[1]}")).Status);
        Assert.Equal(["Opener", "Closer"], await Titles());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"queue/{ids[1]}")).Status);
    }

    [Theory]
    [InlineData("""{"song":"../secret.mid"}""", HttpStatusCode.NotFound)]
    [InlineData("""{"song":"Nope.mid"}""", HttpStatusCode.NotFound)]
    [InlineData("""{}""", HttpStatusCode.BadRequest)]
    public async Task Only_library_songs_can_be_queued(string json, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await Send(HttpMethod.Post, "queue", json)).Status);
        Assert.Empty(await Titles());
    }

    [Fact]
    public async Task Move_needs_a_target()
    {
        var (_, reply) = await Queue("Opener.mid");
        var id = reply.GetProperty("song").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"queue/{id}/move", "{}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "queue/zz/move", """{"to":0}""")).Status);
    }

    [Fact]
    public async Task Guests_see_the_queue_but_cannot_edit_it()
    {
        var guest = await Token(Role.Guest, "table 4");
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "library", token: guest)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "queue", token: guest)).Status);
        var (status, body) = await Queue("Opener.mid", token: guest);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("needs \"queue.edit\"", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_sets_have_no_library_to_queue_from()
    {
        await using var api = ControlApi.Start(new ApiEndpoint(IPAddress.Loopback, 0), Owner, _live, _session);
        using var http = new HttpClient { BaseAddress = api.Address };
        var (status, body) = await Send(HttpMethod.Post, "queue", """{"song":"Opener.mid"}""", http: http);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("metrodom serve", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        var (_, library) = await Send(HttpMethod.Get, "library", http: http);
        Assert.Equal(0, library.GetProperty("songs").GetArrayLength());
    }

    [Fact]
    public async Task The_stream_sends_the_queue_when_it_changes()
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {Owner}");
        await socket.ConnectAsync(new UriBuilder(new Uri(_api.Address, "stream")) { Scheme = "ws" }.Uri, Ct);
        Assert.Equal(0, (await NextQueue(socket)).GetProperty("songs").GetArrayLength());

        await Queue("Ballad.mid");

        var queue = await NextQueue(socket);
        Assert.Equal("Ballad", queue.GetProperty("songs")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_invite_comes_with_a_qr_code_of_its_link()
    {
        var (_, reply) = await Send(HttpMethod.Post, "tokens", """{"role":"guest","label":"table 4"}""");
        var svg = reply.GetProperty("pairingQr").GetString()!;
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Equal(PairingQr.Svg(reply.GetProperty("pairingUrl").GetString()!), svg);
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Request(string song, string token) =>
        Send(HttpMethod.Post, "requests", JsonSerializer.Serialize(new { song }), token);

    [Fact]
    public async Task Guests_request_songs_and_the_crowd_votes_them_up()
    {
        await Queue("Opener.mid");
        _live.Cue(0);
        var table4 = await Token(Role.Guest, "table 4");
        var table7 = await Token(Role.Guest, "table 7");
        var table9 = await Token(Role.Guest, "table 9");

        var (status, reply) = await Request("Ballad.mid", table4);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("request", reply.GetProperty("song").GetProperty("kind").GetString());
        Assert.True(reply.GetProperty("song").GetProperty("mine").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await Request("covers/Closer.mid", table7)).Status);

        // Asking for a song already requested is a vote for it.
        var (again, vote) = await Request("covers/Closer.mid", table9);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.True(vote.GetProperty("countedAsVote").GetBoolean());
        Assert.Equal(["Opener", "Closer", "Ballad"], await Titles());

        var (_, queue) = await Send(HttpMethod.Get, "queue", token: table9);
        var closer = queue.GetProperty("songs")[1];
        Assert.Equal((2, true, false), (closer.GetProperty("votes").GetInt32(), closer.GetProperty("voted").GetBoolean(), closer.GetProperty("mine").GetBoolean()));
        Assert.True(queue.GetProperty("requests").GetProperty("open").GetBoolean());
        Assert.DoesNotContain(table4, queue.GetRawText(), StringComparison.Ordinal);

        var ballad = queue.GetProperty("songs")[2].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"queue/{ballad}/vote", token: table9)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"queue/{ballad}/vote", token: table7)).Status);
        Assert.Equal(["Opener", "Ballad", "Closer"], await Titles()); // 3 votes beat 2
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"queue/{ballad}/vote", token: table7)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"queue/{ballad}/vote", token: table7)).Status);
    }

    [Fact]
    public async Task Guest_limits_answer_429_with_retry_after()
    {
        var guest = await Token(Role.Guest, "table 4");
        Assert.Equal(HttpStatusCode.Created, (await Request("Ballad.mid", guest)).Status);

        using var request = new HttpRequestMessage(HttpMethod.Post, "requests") { Content = new StringContent("""{"song":"Opener.mid"}""", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", guest);
        using var response = await _http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 290, 300); // crowd-jukebox: one every 5 min

        var (_, status) = await Send(HttpMethod.Get, "requests", token: guest);
        Assert.Equal(1, status.GetProperty("you").GetProperty("waiting").GetInt32());
        Assert.InRange(status.GetProperty("you").GetProperty("nextRequestInSeconds").GetDouble(), 290, 300);
    }

    [Fact]
    public async Task The_owner_pauses_requests_and_guests_cannot()
    {
        var guest = await Token(Role.Guest, "table 4");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, "requests", """{"open":false}""", guest)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "requests", """{"open":false}""")).Status);

        var (status, body) = await Request("Ballad.mid", guest);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("paused", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.False((await Send(HttpMethod.Get, "queue")).Body.GetProperty("requests").GetProperty("open").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "requests", """{"open":true}""")).Status);
        Assert.Equal(HttpStatusCode.Created, (await Request("Ballad.mid", guest)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "requests", "{}")).Status);
    }

    [Fact]
    public async Task Requests_need_a_library_song()
    {
        var guest = await Token(Role.Guest, "table 4");
        Assert.Equal(HttpStatusCode.NotFound, (await Request("../secret.mid", guest)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "requests", "{}", guest)).Status);
        Assert.Empty(await Titles());
    }

    [Fact]
    public async Task Play_sets_take_no_requests()
    {
        await using var api = ControlApi.Start(new ApiEndpoint(IPAddress.Loopback, 0), Owner, _live, _session);
        using var http = new HttpClient { BaseAddress = api.Address };
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, "requests", """{"song":"Opener.mid"}""", http: http)).Status);
        var (_, requests) = await Send(HttpMethod.Get, "requests", http: http);
        Assert.False(requests.GetProperty("open").GetBoolean());
    }

    private static async Task<JsonElement> NextQueue(ClientWebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            var element = JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement;
            if (element.GetProperty("type").GetString() == "queue")
            {
                return element.GetProperty("queue").Clone();
            }
        }
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
