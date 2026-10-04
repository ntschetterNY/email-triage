using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmailTriage.Companion;

/// <summary>A broadcast message on the relay: "req" from a device, "res" from the PC.</summary>
public sealed record RelayMessage(string Event, RelayChunk Chunk);

/// <summary>One connection to the relay channel. Made fresh for each attempt; dispose to leave.</summary>
public interface IRelayChannel : IAsyncDisposable
{
    Task JoinAsync(CancellationToken ct);

    /// <summary>The next broadcast from someone else on the channel; throws when the connection drops.</summary>
    Task<RelayMessage> ReceiveAsync(CancellationToken ct);

    Task SendAsync(RelayMessage message, CancellationToken ct);
}

/// <summary>
/// A Supabase Realtime broadcast channel, spoken to directly over its
/// Phoenix WebSocket protocol. The connection goes out from this PC on 443,
/// which a Public network profile and a locked-down firewall both allow.
/// </summary>
public sealed class RealtimeChannel : IRelayChannel
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(70);

    private readonly Uri _socketUri;
    private readonly string _topic;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private int _ref;
    private string _joinRef = "";
    private long _lastHeard = Environment.TickCount64;
    private Task? _heartbeat;

    /// <param name="projectUrl">The Supabase project, e.g. https://abcd.supabase.co.</param>
    /// <param name="apiKey">Its publishable (anon) key.</param>
    /// <param name="channel">The channel name, from <see cref="RelaySeal.Channel"/>.</param>
    public RealtimeChannel(Uri projectUrl, string apiKey, string channel)
    {
        _socketUri = SocketUri(projectUrl, apiKey);
        _topic = "realtime:" + channel;
    }

    public static Uri SocketUri(Uri projectUrl, string apiKey)
    {
        var builder = new UriBuilder(projectUrl)
        {
            Scheme = projectUrl.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Path = "/realtime/v1/websocket",
            Query = $"apikey={Uri.EscapeDataString(apiKey)}&vsn=1.0.0",
        };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    public async Task JoinAsync(CancellationToken ct)
    {
        await _socket.ConnectAsync(_socketUri, ct).ConfigureAwait(false);

        _joinRef = NextRef();
        await SendFrameAsync(new JsonObject
        {
            ["topic"] = _topic,
            ["event"] = "phx_join",
            ["payload"] = new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["broadcast"] = new JsonObject { ["ack"] = false, ["self"] = false },
                    ["presence"] = new JsonObject { ["key"] = "", ["enabled"] = false },
                    ["postgres_changes"] = new JsonArray(),
                    ["private"] = false,
                },
            },
            ["ref"] = _joinRef,
            ["join_ref"] = _joinRef,
        }, ct).ConfigureAwait(false);

        while (true)
        {
            using var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
            var root = frame.RootElement;
            if (Str(root, "event") != "phx_reply" || Str(root, "ref") != _joinRef) continue;

            var payload = root.GetProperty("payload");
            if (Str(payload, "status") == "ok") break;
            var reason = payload.TryGetProperty("response", out var response) ? response.ToString() : "refused";
            throw new WebSocketException($"The relay refused to join: {reason}");
        }

        _heartbeat = HeartbeatAsync(_closing.Token);
    }

    public async Task<RelayMessage> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            using var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
            var root = frame.RootElement;

            switch (Str(root, "event"))
            {
                case "broadcast" when Str(root, "topic") == _topic:
                    var outer = root.GetProperty("payload");
                    if (outer.TryGetProperty("payload", out var inner)
                        && Str(outer, "event") is { } name
                        && inner.Deserialize<RelayChunk>(RelaySeal.Json) is { } chunk)
                        return new RelayMessage(name, chunk);
                    break;

                case "phx_error":
                case "phx_close" when Str(root, "topic") == _topic:
                    throw new WebSocketException("The relay closed the channel.");
            }
        }
    }

    public Task SendAsync(RelayMessage message, CancellationToken ct) =>
        SendFrameAsync(new JsonObject
        {
            ["topic"] = _topic,
            ["event"] = "broadcast",
            ["payload"] = new JsonObject
            {
                ["type"] = "broadcast",
                ["event"] = message.Event,
                ["payload"] = JsonSerializer.SerializeToNode(message.Chunk, RelaySeal.Json),
            },
            ["ref"] = NextRef(),
            ["join_ref"] = _joinRef,
        }, ct);

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        if (_heartbeat is not null) await _heartbeat.ConfigureAwait(false);

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
        _socket.Dispose();
        _sending.Dispose();
        _closing.Dispose();
    }

    /// <summary>Keeps the channel alive, and drops a connection that has gone quiet so the caller reconnects.</summary>
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatEvery, ct).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastHeard) > DeadAfter.TotalMilliseconds)
                {
                    _socket.Abort();
                    return;
                }
                await SendFrameAsync(new JsonObject
                {
                    ["topic"] = "phoenix",
                    ["event"] = "heartbeat",
                    ["payload"] = new JsonObject(),
                    ["ref"] = NextRef(),
                }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Closing, or the socket already failed; the receive loop reports it.
        }
    }

    private async Task SendFrameAsync(JsonObject frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { _sending.Release(); }
    }

    private async Task<JsonDocument> ReadFrameAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("The relay closed the connection.");
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        Interlocked.Exchange(ref _lastHeard, Environment.TickCount64);
        return JsonDocument.Parse(buffer.ToArray());
    }

    private string NextRef() => Interlocked.Increment(ref _ref).ToString();

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
