using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using EmailTriage.Companion;
using EmailTriage.Core.Data;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class RelaySealTests
{
    private static readonly byte[] Key = RelaySeal.NewKey();

    [Fact]
    public void Sealed_message_opens_with_the_same_key_and_direction()
    {
        var text = "{\"hello\":\"" + new string('x', 5000) + "\"}";

        var sealedText = RelaySeal.Seal(Key, Encoding.UTF8.GetBytes(text), request: true);

        Assert.DoesNotContain("hello", sealedText);
        Assert.Equal(text, RelaySeal.Text(RelaySeal.Open(Key, sealedText, request: true)));
    }

    [Fact]
    public void Sealed_message_does_not_open_with_another_key_or_as_the_other_direction()
    {
        var sealedText = RelaySeal.Seal(Key, "secret"u8, request: true);

        Assert.ThrowsAny<CryptographicException>(() => RelaySeal.Open(RelaySeal.NewKey(), sealedText, request: true));
        Assert.ThrowsAny<CryptographicException>(() => RelaySeal.Open(Key, sealedText, request: false));
        Assert.ThrowsAny<CryptographicException>(() => RelaySeal.Open(Key, "not base64!", request: true));
    }

    [Fact]
    public void Tampered_message_does_not_open()
    {
        var bytes = Convert.FromBase64String(RelaySeal.Seal(Key, "archive everything"u8, request: true));
        bytes[^1] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => RelaySeal.Open(Key, Convert.ToBase64String(bytes), request: true));
    }

    [Fact]
    public void Channel_comes_from_the_key_and_gives_nothing_of_it_away()
    {
        var channel = RelaySeal.Channel(Key);

        Assert.Equal(channel, RelaySeal.Channel(Key.ToArray()));
        Assert.NotEqual(channel, RelaySeal.Channel(RelaySeal.NewKey()));
        Assert.StartsWith("email-triage-", channel);
        Assert.DoesNotContain(Convert.ToHexString(Key).ToLowerInvariant()[..8], channel);
    }

    [Fact]
    public void Big_message_is_cut_into_chunks_that_come_back_together_in_any_order()
    {
        var sealedText = new string('a', RelaySeal.ChunkSize) + new string('b', RelaySeal.ChunkSize) + "c";
        var chunks = RelaySeal.Chunk("id", sealedText);
        var assembler = new RelayAssembler();

        Assert.Equal(3, chunks.Count);
        Assert.Null(assembler.Add(chunks[2]));
        Assert.Null(assembler.Add(chunks[0]));
        Assert.Null(assembler.Add(chunks[0]));
        Assert.Equal(sealedText, assembler.Add(chunks[1]));
    }

    [Fact]
    public void Chunks_that_make_no_sense_are_ignored()
    {
        var assembler = new RelayAssembler();

        Assert.Null(assembler.Add(new RelayChunk("id", 3, 3, "x")));
        Assert.Null(assembler.Add(new RelayChunk("id", 0, RelaySeal.MaxChunks + 1, "x")));
        Assert.Null(assembler.Add(new RelayChunk("", 0, 1, "x")));
        Assert.Equal("x", assembler.Add(new RelayChunk("id", 0, 1, "x")));
    }

    [Fact]
    public void Relay_settings_need_an_https_address_and_a_key()
    {
        Assert.Equal(new Uri("https://abcd.supabase.co"), RelaySettings.From(" https://abcd.supabase.co/rest/v1 ", "k")!.ProjectUrl);
        Assert.Null(RelaySettings.From("http://abcd.supabase.co", "k"));
        Assert.Null(RelaySettings.From("https://abcd.supabase.co", " "));
        Assert.Null(RelaySettings.From("", "k"));
    }

    [Fact]
    public void Realtime_socket_address_carries_the_key()
    {
        var uri = RealtimeChannel.SocketUri(new Uri("https://abcd.supabase.co"), "sb_publishable_x");

        Assert.Equal("wss://abcd.supabase.co/realtime/v1/websocket?apikey=sb_publishable_x&vsn=1.0.0", uri.ToString());
    }

    [Fact]
    public void Identity_keeps_its_relay_key_and_a_reset_replaces_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"companion-{Guid.NewGuid():N}.key");
        try
        {
            var first = CompanionIdentity.LoadOrCreate(path);
            Assert.Equal(32, first.RelayKey.Length);
            Assert.Equal(first.RelayKey, CompanionIdentity.LoadOrCreate(path).RelayKey);
            Assert.NotEqual(first.RelayKey, CompanionIdentity.Create(path).RelayKey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Identity_saved_before_the_relay_gains_a_key_and_keeps_its_pairing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"companion-{Guid.NewGuid():N}.key");
        try
        {
            var first = CompanionIdentity.LoadOrCreate(path);
            // What the file held before relay keys existed.
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            var json = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
            json.Remove("RelayKey");
            bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, bytes);

            var upgraded = CompanionIdentity.LoadOrCreate(path);

            Assert.Equal(first.Token, upgraded.Token);
            Assert.Equal(first.Fingerprint, upgraded.Fingerprint);
            Assert.Equal(32, upgraded.RelayKey.Length);
            Assert.Equal(upgraded.RelayKey, CompanionIdentity.LoadOrCreate(path).RelayKey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pairing_link_carries_the_relay_when_there_is_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"companion-{Guid.NewGuid():N}.key");
        try
        {
            var identity = CompanionIdentity.LoadOrCreate(path);
            var hosts = new[] { IPAddress.Parse("192.168.1.20") };

            Assert.DoesNotContain("&k=", Pairing.Link(hosts, 47821, identity, "PC"));

            var link = Pairing.Link(hosts, 47821, identity, "PC", RelaySettings.From("https://abcd.supabase.co", "sb_publishable_x"));
            Assert.Contains("&r=https%3A%2F%2Fabcd.supabase.co&", link);
            Assert.Contains("&a=sb_publishable_x&", link);
            var k = link[(link.IndexOf("&k=", StringComparison.Ordinal) + 3)..];
            Assert.Equal(identity.RelayKey, Convert.FromBase64String(k.Replace('-', '+').Replace('_', '/') + "="));
        }
        finally { File.Delete(path); }
    }
}

/// <summary>The relay host in front of the real server, with an in-memory relay standing in for Supabase.</summary>
public class RelayHostTests : IAsyncLifetime
{
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"companion-{Guid.NewGuid():N}.key");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");
    private readonly FakeRelay _relay = new();
    private CompanionIdentity _identity = null!;
    private CompanionServer _server = null!;
    private RelayHost _host = null!;
    private Database _db = null!;
    private FakeRelay.End _phone = null!;

    public async Task InitializeAsync()
    {
        _identity = CompanionIdentity.LoadOrCreate(_keyPath);
        _db = new Database(_dbPath);
        _db.Migrate();

        var clock = new FakeClock();
        var store = new FakeMailStore();
        await store.ConnectAsync();
        var service = new CompanionService(store, new SnoozeRepository(_db, clock), new ActionItemRepository(_db, clock),
            new FolderSearchService(store, new FakeFolderUsage(), clock), clock,
            () => new CompanionOptions { PcName = "TEST-PC", Version = "1.0.test" });

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _server = new CompanionServer(service, _identity, port);
        await _server.StartAsync();

        _host = new RelayHost(_relay.Join, _identity, port);
        var connected = new TaskCompletionSource();
        _host.StateChanged += (_, _) => { if (_host.State == RelayState.Connected) connected.TrySetResult(); };
        _host.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _phone = _relay.Join();
        await _phone.JoinAsync(default);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_keyPath); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private RelayRequest Request(string path, string method = "GET", string? body = null, DateTimeOffset? at = null) =>
        new(Guid.NewGuid().ToString("N"), (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(), method, path, body);

    private async Task SendAsync(RelayRequest request, byte[]? key = null)
    {
        var sealedText = RelaySeal.Seal(key ?? _identity.RelayKey, RelaySeal.Encode(request), request: true);
        foreach (var chunk in RelaySeal.Chunk(request.Id, sealedText))
            await _phone.SendAsync(new RelayMessage("req", chunk), default);
    }

    private async Task<RelayResponse?> AnswerAsync(TimeSpan? wait = null)
    {
        var assembler = new RelayAssembler();
        using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var message = await _phone.ReceiveAsync(timeout.Token);
                if (message.Event == "res" && assembler.Add(message.Chunk) is { } sealedText)
                    return RelaySeal.Decode<RelayResponse>(RelaySeal.Open(_identity.RelayKey, sealedText, request: false));
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    [Fact]
    public async Task Request_through_the_relay_is_answered_by_the_server()
    {
        var request = Request("/api/hello");
        await SendAsync(request);

        var answer = await AnswerAsync();

        Assert.Equal(request.Id, answer!.Id);
        Assert.Equal(200, answer.Status);
        Assert.Contains("TEST-PC", answer.Body);
    }

    [Fact]
    public async Task Refusals_come_back_with_their_status()
    {
        await SendAsync(Request("/api/archive", "POST", "{\"refs\":[]}"));

        var answer = await AnswerAsync();

        Assert.Equal(400, answer!.Status);
        Assert.Contains("No messages given.", answer.Body);
    }

    [Fact]
    public async Task Request_sealed_with_another_key_gets_no_answer()
    {
        await SendAsync(Request("/api/hello"), key: RelaySeal.NewKey());

        Assert.Null(await AnswerAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Replayed_request_is_answered_once()
    {
        var request = Request("/api/hello");
        await SendAsync(request);
        Assert.NotNull(await AnswerAsync());

        await SendAsync(request);

        Assert.Null(await AnswerAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Stale_request_gets_no_answer()
    {
        await SendAsync(Request("/api/hello", at: DateTimeOffset.UtcNow.AddMinutes(-10)));

        Assert.Null(await AnswerAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Only_the_api_is_reachable()
    {
        await SendAsync(Request("http://example.com/api/hello"));

        Assert.Null(await AnswerAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Host_reconnects_when_the_relay_drops()
    {
        var reconnected = new TaskCompletionSource();
        _host.StateChanged += (_, _) => { if (_host.State == RelayState.Connected) reconnected.TrySetResult(); };

        _relay.DropAll(except: _phone);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await SendAsync(Request("/api/hello"));
        Assert.Equal(200, (await AnswerAsync())!.Status);
    }
}

/// <summary>A broadcast channel in memory: what one end sends, every other end receives.</summary>
internal sealed class FakeRelay
{
    private readonly List<End> _ends = new();

    public End Join()
    {
        var end = new End(this);
        lock (_ends) _ends.Add(end);
        return end;
    }

    public void DropAll(End except)
    {
        lock (_ends)
            foreach (var end in _ends.Where(e => e != except)) end.Inbox.Writer.TryComplete(new IOException("dropped"));
    }

    private void Broadcast(End from, RelayMessage message)
    {
        lock (_ends)
            foreach (var end in _ends.Where(e => e != from)) end.Inbox.Writer.TryWrite(message);
    }

    internal sealed class End(FakeRelay relay) : IRelayChannel
    {
        public Channel<RelayMessage> Inbox { get; } = Channel.CreateUnbounded<RelayMessage>();

        public Task JoinAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<RelayMessage> ReceiveAsync(CancellationToken ct) => await Inbox.Reader.ReadAsync(ct);

        public Task SendAsync(RelayMessage message, CancellationToken ct)
        {
            relay.Broadcast(this, message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            lock (relay._ends) relay._ends.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
