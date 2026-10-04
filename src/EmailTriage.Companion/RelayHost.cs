using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace EmailTriage.Companion;

public enum RelayState { Off, Connecting, Connected, Retrying }

/// <summary>
/// The PC's end of the relay, for when a device can't reach this PC
/// directly: a Public network profile blocks connections coming in, and
/// guest Wi-Fi keeps devices apart. The PC dials out to the relay instead,
/// opens each sealed request, hands it to the companion server on loopback
/// (so the token check and every endpoint stay exactly as they are), and
/// seals the answer back.
/// </summary>
public sealed class RelayHost : IAsyncDisposable
{
    /// <summary>A request older than this, or from further ahead, is dropped: a relay replaying traffic gets nowhere.</summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>Spacing between the chunks of a big answer, to stay inside the relay's messages-per-second quota.</summary>
    private static readonly TimeSpan ChunkSpacing = TimeSpan.FromMilliseconds(20);

    private readonly Func<IRelayChannel> _connect;
    private readonly byte[] _key;
    private readonly HttpClient _local;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _seen = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <param name="connect">Makes a fresh connection to the relay channel for each attempt.</param>
    /// <param name="identity">Holds the relay key, and the token and certificate the local server expects.</param>
    /// <param name="port">The companion server's port, reached on loopback.</param>
    public RelayHost(Func<IRelayChannel> connect, CompanionIdentity identity, int port, TimeProvider? time = null)
    {
        _connect = connect;
        _key = identity.RelayKey;
        _time = time ?? TimeProvider.System;

        var pin = identity.Fingerprint;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(pin, StringComparison.OrdinalIgnoreCase),
        };
        _local = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        _local.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
    }

    public RelayState State { get; private set; } = RelayState.Off;

    /// <summary>Why the last attempt failed, while retrying.</summary>
    public string? Problem { get; private set; }

    public event EventHandler? StateChanged;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stopping.Token));

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _local.Dispose();
        _stopping.Dispose();
        SetState(RelayState.Off, null);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(failures == 0 ? RelayState.Connecting : RelayState.Retrying, Problem);
            try
            {
                await using var channel = _connect();
                using (var joinTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    joinTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await channel.JoinAsync(joinTimeout.Token).ConfigureAwait(false);
                }
                failures = 0;
                SetState(RelayState.Connected, null);

                var assembler = new RelayAssembler(_time);
                while (true)
                {
                    var message = await channel.ReceiveAsync(ct).ConfigureAwait(false);
                    if (message.Event != "req") continue;
                    if (assembler.Add(message.Chunk) is { } sealedText)
                        _ = AnswerAsync(channel, sealedText, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Problem = ex.Message;
                SetState(RelayState.Retrying, Problem);
            }

            await Task.Delay(Backoff[Math.Min(failures++, Backoff.Length - 1)], _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Opens, checks, forwards and answers one request. Anything that doesn't open is ignored: it wasn't from a paired device.</summary>
    private async Task AnswerAsync(IRelayChannel channel, string sealedText, CancellationToken ct)
    {
        try
        {
            var request = Open(sealedText);
            if (request is null) return;

            var response = await ForwardAsync(request, ct).ConfigureAwait(false);
            var chunks = RelaySeal.Chunk(request.Id, RelaySeal.Seal(_key, RelaySeal.Encode(response), request: false));
            foreach (var chunk in chunks)
            {
                await channel.SendAsync(new RelayMessage("res", chunk), ct).ConfigureAwait(false);
                if (chunks.Count > 1) await Task.Delay(ChunkSpacing, _time, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or System.Net.WebSockets.WebSocketException
                                       or ObjectDisposedException or CompanionException)
        {
            // Stopping, the connection dropped, or the answer was too big: the
            // device times out and says so.
        }
    }

    /// <summary>The request, when it was sealed with this PC's key, is fresh, and hasn't been seen before.</summary>
    internal RelayRequest? Open(string sealedText)
    {
        RelayRequest? request;
        try { request = RelaySeal.Decode<RelayRequest>(RelaySeal.Open(_key, sealedText, request: true)); }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException or InvalidDataException) { return null; }

        if (request is null || string.IsNullOrEmpty(request.Id)) return null;
        if (request.Method is not ("GET" or "POST")) return null;
        if (!request.Path.StartsWith("/api/", StringComparison.Ordinal)) return null;

        var now = _time.GetUtcNow();
        if ((now - DateTimeOffset.FromUnixTimeMilliseconds(request.At)).Duration() > Freshness) return null;

        lock (_seen)
        {
            foreach (var old in _seen.Where(s => now - s.Value > Freshness * 2).Select(s => s.Key).ToList())
                _seen.Remove(old);
            if (!_seen.TryAdd(request.Id, now)) return null;
        }
        return request;
    }

    private async Task<RelayResponse> ForwardAsync(RelayRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Path);
        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        using var response = await _local.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new RelayResponse(request.Id, (int)response.StatusCode, body);
    }

    private void SetState(RelayState state, string? problem)
    {
        if (State == state && Problem == problem) return;
        State = state;
        Problem = problem;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
