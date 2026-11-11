using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MetroDom.Conductor.Commands;
using Microsoft.AspNetCore.Http;

namespace MetroDom.Conductor.Api;

/// <summary>
/// <c>GET /api/v0/stream</c>: live state over a WebSocket (ADR 0003), events as in
/// <c>events.schema.json</c>. Each connection polls the same snapshot as <c>GET /state</c> and
/// sends only what changed, so clients never poll and the band's threads never wait on a socket.
/// </summary>
internal sealed class StateStream(LiveControl live, Session session)
{
    public const string Subprotocol = "metrodom.v0";

    /// <summary>Browsers can't set headers on a WebSocket, so the token may come as this subprotocol prefix.</summary>
    public const string TokenPrefix = "token.";

    /// <summary>Enough for a few phones and a display; each stream is a small polling loop.</summary>
    public const int MaxStreams = 16;

    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan PositionEvery = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private int _open;

    /// <summary>Streams open now.</summary>
    public int Open => Volatile.Read(ref _open);

    public async Task<IResult> Accept(HttpContext context, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.Json(new { error = "This is a WebSocket: connect with Upgrade: websocket (subprotocol metrodom.v0)." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var offered = context.WebSockets.WebSocketRequestedProtocols;
        var named = offered.Where(p => !p.StartsWith(TokenPrefix, StringComparison.Ordinal)).ToArray();
        if (named.Length > 0 && !named.Contains(Subprotocol))
        {
            return Results.Json(new { error = $"Unsupported subprotocol: offer {Subprotocol}." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (Interlocked.Increment(ref _open) > MaxStreams)
        {
            Interlocked.Decrement(ref _open);
            return Results.Json(new { error = $"Too many streams open (max {MaxStreams})." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(named.Length > 0 ? Subprotocol : null).ConfigureAwait(false);
            using var done = CancellationTokenSource.CreateLinkedTokenSource(stopping, context.RequestAborted);
            await Run(socket, done).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _open);
        }

        return Results.Empty;
    }

    private async Task Run(WebSocket socket, CancellationTokenSource done)
    {
        var messages = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnMessage(string text) => messages.Writer.TryWrite(text);
        session.Messages.Added += OnMessage;
        using var abort = new CancellationTokenSource();
        var reading = ReadUntilClosed(socket, done, abort.Token);
        var seq = 0L;
        try
        {
            await Send(socket, new { type = "hello", seq = ++seq, api = "v0", positionEveryMs = (int)PositionEvery.TotalMilliseconds }, done.Token).ConfigureAwait(false);
            string? lastState = null;
            string? lastInstruments = null;
            var lastPosition = DateTime.MinValue;
            while (!done.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var snapshot = live.Snapshot();
                var state = JsonSerializer.Serialize(ControlApi.State(snapshot with { Position = null }), Json);
                if (state != lastState)
                {
                    lastState = state;
                    await SendRaw(socket, $"{{\"type\":\"state\",\"seq\":{++seq},\"state\":{state}}}", done.Token).ConfigureAwait(false);
                }

                var instruments = JsonSerializer.Serialize(ControlApi.Instruments(session), Json);
                if (instruments != lastInstruments)
                {
                    lastInstruments = instruments;
                    await SendRaw(socket, $"{{\"type\":\"instruments\",\"seq\":{++seq},\"instruments\":{instruments}}}", done.Token).ConfigureAwait(false);
                }

                if (snapshot is { Playing: true, Position: { } position, Duration: { } duration } && DateTime.UtcNow - lastPosition >= PositionEvery)
                {
                    lastPosition = DateTime.UtcNow;
                    await Send(socket, new { type = "position", seq = ++seq, positionSeconds = position.TotalSeconds, durationSeconds = duration.TotalSeconds }, done.Token).ConfigureAwait(false);
                }

                while (messages.Reader.TryRead(out var text))
                {
                    await Send(socket, new { type = "message", seq = ++seq, text }, done.Token).ConfigureAwait(false);
                }

                await Task.Delay(Poll, done.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The client went away, or the set is ending.
        }
        finally
        {
            session.Messages.Added -= OnMessage;
            await Close(socket).ConfigureAwait(false);

            // Give the client a moment to answer the close; cancelling a pending read aborts the socket.
            if (await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false) != reading)
            {
                await abort.CancelAsync().ConfigureAwait(false);
            }

            await reading.ConfigureAwait(false);
        }
    }

    /// <summary>Answers the client's close, or starts one when the set ends.</summary>
    private static async Task Close(WebSocket socket)
    {
        var (status, reason) = socket.State switch
        {
            WebSocketState.Open => (WebSocketCloseStatus.EndpointUnavailable, "The set has ended."),
            WebSocketState.CloseReceived => (WebSocketCloseStatus.NormalClosure, "Bye."),
            _ => ((WebSocketCloseStatus?)null, ""),
        };
        if (status is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await socket.CloseOutputAsync(status.Value, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    /// <summary>Clients only listen in v0: read (and drop) frames until they close, then stop the stream.</summary>
    private static async Task ReadUntilClosed(WebSocket socket, CancellationTokenSource done, CancellationToken abort)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(buffer, abort).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }

        await done.CancelAsync().ConfigureAwait(false);
    }

    private static Task Send(WebSocket socket, object payload, CancellationToken cancellationToken) =>
        SendRaw(socket, JsonSerializer.Serialize(payload, Json), cancellationToken);

    private static Task SendRaw(WebSocket socket, string json, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}
