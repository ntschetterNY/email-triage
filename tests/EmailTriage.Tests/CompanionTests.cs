using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using EmailTriage.Companion;
using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class CompanionServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");
    private readonly string _images = Path.Combine(Path.GetTempPath(), $"triage-img-{Guid.NewGuid():N}");
    private readonly Database _db;
    private readonly FakeClock _clock = new();
    private readonly FakeMailStore _store = new();
    private readonly SnoozeRepository _snoozes;
    private readonly ActionItemRepository _actions;
    private readonly CompanionService _service;

    public CompanionServiceTests()
    {
        _db = new Database(_dbPath);
        _db.Migrate();
        _snoozes = new SnoozeRepository(_db, _clock);
        _actions = new ActionItemRepository(_db, _clock);
        _store.ConnectAsync().GetAwaiter().GetResult();

        var folders = new FolderSearchService(_store, new FakeFolderUsage(), _clock);
        _service = new CompanionService(_store, _snoozes, _actions, folders, _clock,
            () => new CompanionOptions { InlineImageFolder = _images });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_images, true); } catch { }
    }

    private MailSummary Mail(string id, string conversation, int minutesAgo, bool unread = false, params string[] categories) => new()
    {
        Ref = new MailRef(id, "store"),
        InternetMessageId = $"<{id}@corp.com>",
        Subject = $"Subject {conversation}",
        SenderName = "Alice",
        SenderAddress = "alice@corp.com",
        ReceivedUtc = _clock.UtcNow.AddMinutes(-minutesAgo),
        IsUnread = unread,
        HasAttachments = false,
        ConversationKey = conversation,
        Categories = categories,
    };

    private async Task<InboxDto> SeedInboxAsync(params MailSummary[] inbox)
    {
        _store.MailByFolder["inbox"] = inbox.ToList();
        return await _service.GetInboxAsync();
    }

    [Fact]
    public async Task Inbox_groups_conversations_newest_first_with_their_inbox_refs()
    {
        _store.MailByFolder["sent"] = new() { Mail("s1", "A", 1) };
        var inbox = await SeedInboxAsync(
            Mail("a1", "A", 30), Mail("a2", "A", 10, unread: true),
            Mail("b1", "B", 5, false, "Action Required"));

        Assert.Equal(new[] { "A", "B" }, inbox.Conversations.Select(c => c.Key));

        var a = inbox.Conversations[0];
        Assert.True(a.Unread);
        Assert.True(a.LatestIsMine);
        Assert.False(a.Flagged);
        Assert.Equal(3, a.Count);
        Assert.Equal("a2", a.ReplyTo.E);
        Assert.Equal(new[] { "a2", "a1" }, a.Inbox.Select(r => r.E));
        Assert.Equal(new[] { "s1", "a2", "a1" }, a.Messages.Select(r => r.E));
        Assert.Equal("mail", a.Kind);

        Assert.True(inbox.Conversations[1].Flagged);
    }

    [Fact]
    public async Task Archive_moves_every_message_to_the_archive()
    {
        await SeedInboxAsync(Mail("a1", "A", 30), Mail("a2", "A", 10));

        var done = await _service.ArchiveAsync(new RefsRequest(new[] { new RefDto("a1", "store"), new RefDto("a2", "store") }));

        Assert.Equal(2, done);
        Assert.All(_store.Moves, m => Assert.Equal("Mailbox\\Archive", m.Target.Path));
    }

    [Fact]
    public async Task Archive_that_moves_nothing_reports_why()
    {
        await SeedInboxAsync(Mail("a1", "A", 30));
        _store.NextMoveFailure = new InvalidOperationException("The item has been deleted.");

        var ex = await Assert.ThrowsAsync<CompanionException>(() =>
            _service.ArchiveAsync(new RefsRequest(new[] { new RefDto("a1", "store") })));

        Assert.Equal(409, ex.Status);
        Assert.Contains("deleted", ex.Message);
    }

    [Fact]
    public async Task Snooze_parks_each_message_with_an_entry_the_desktop_scheduler_returns()
    {
        await SeedInboxAsync(Mail("a1", "A", 30));
        var when = _clock.UtcNow.AddDays(1);

        await _service.SnoozeAsync(new SnoozeRequest(new[] { new RefDto("a1", "store") }, when));

        var move = Assert.Single(_store.Moves);
        Assert.Equal("Mailbox\\Snoozed", move.Target.Path);

        var entry = Assert.Single(await _snoozes.GetPendingAsync());
        Assert.Equal("<a1@corp.com>", entry.InternetMessageId);
        Assert.Equal("a1-moved", entry.EntryId);
        Assert.Equal("inbox", entry.OriginFolderEntryId);
        Assert.Equal(when, entry.ReturnUtc);
    }

    [Fact]
    public async Task Snooze_of_a_message_the_phone_has_not_seen_asks_for_a_refresh()
    {
        await SeedInboxAsync(Mail("a1", "A", 30));

        var ex = await Assert.ThrowsAsync<CompanionException>(() =>
            _service.SnoozeAsync(new SnoozeRequest(new[] { new RefDto("zzz", "store") }, _clock.UtcNow.AddHours(1))));

        Assert.Equal(409, ex.Status);
        Assert.Empty(_store.Moves);
    }

    [Fact]
    public async Task Snooze_into_the_past_is_refused()
    {
        await SeedInboxAsync(Mail("a1", "A", 30));

        var ex = await Assert.ThrowsAsync<CompanionException>(() =>
            _service.SnoozeAsync(new SnoozeRequest(new[] { new RefDto("a1", "store") }, _clock.UtcNow.AddMinutes(-1))));

        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Snooze_text_is_read_like_the_desktop_palette()
    {
        var parsed = _service.ParseSnooze("3d");

        Assert.NotNull(parsed);
        Assert.Equal(_clock.Now.AddDays(3), parsed!.When);
        Assert.Null(_service.ParseSnooze("whenever"));
        Assert.NotEmpty(_service.SnoozeOptions());
    }

    [Fact]
    public async Task Flag_puts_the_newest_message_on_the_board_and_unflag_takes_it_off()
    {
        await SeedInboxAsync(Mail("a1", "A", 30), Mail("a2", "A", 10));
        var refs = new[] { new RefDto("a2", "store"), new RefDto("a1", "store") };

        await _service.SetFlagAsync(new FlagRequest(refs, true));

        var card = Assert.Single(await _actions.GetOpenAsync());
        Assert.Equal("<a2@corp.com>", card.InternetMessageId);
        Assert.Equal((new MailRef("a2", "store"), "Action Required", true), _store.CategoryWrites.Single());

        await _service.SetFlagAsync(new FlagRequest(refs, false));

        Assert.Empty(await _actions.GetOpenAsync());
        Assert.Equal(2, _store.CategoryWrites.Count(w => !w.On));
    }

    [Fact]
    public async Task Reply_sends_through_outlook_and_can_archive_the_thread()
    {
        await SeedInboxAsync(Mail("a1", "A", 30));

        await _service.ReplyAsync(new ReplyRequest(
            new RefDto("a1", "store"), "sender", "Thanks <b>all</b>\nSee you then",
            new[] { new RefDto("a1", "store") }));

        Assert.Equal(ReplyScope.SenderOnly, Assert.Single(_store.RepliesBuilt).Scope);
        var sent = Assert.Single(_store.RepliesSent);
        Assert.Contains("Thanks &lt;b&gt;all&lt;/b&gt;", sent.Html);
        Assert.Contains("See you then", sent.Html);
        Assert.Equal("Mailbox\\Archive", Assert.Single(_store.Moves).Target.Path);
    }

    [Fact]
    public async Task Reply_that_fails_to_send_leaves_no_draft_behind()
    {
        _store.NextSendFailure = new InvalidOperationException("Recipient could not be resolved.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ReplyAsync(new ReplyRequest(new RefDto("a1", "store"), "all", "ok")));

        Assert.Equal("reply-a1", Assert.Single(_store.DraftsDiscarded));
        Assert.Empty(_store.Moves);
    }

    [Fact]
    public async Task Empty_reply_is_refused_before_outlook_is_touched()
    {
        var ex = await Assert.ThrowsAsync<CompanionException>(() =>
            _service.ReplyAsync(new ReplyRequest(new RefDto("a1", "store"), "all", "   ")));

        Assert.Equal(400, ex.Status);
        Assert.Empty(_store.RepliesBuilt);
    }

    [Fact]
    public async Task Thread_renders_the_messages_that_open_and_skips_the_ones_that_vanished()
    {
        _store.Bodies["a2"] = new MailBody
        {
            Ref = new MailRef("a2", "store"),
            Subject = "Hello",
            SenderName = "Alice",
            SenderAddress = "alice@corp.com",
            ReceivedUtc = _clock.UtcNow,
            Html = "<p>Body <script>alert(1)</script>text</p>",
        };

        var page = await _service.GetThreadAsync(new ThreadRequest(
            new[] { new RefDto("a2", "store"), new RefDto("gone", "store") }, Dark: false));

        Assert.Equal(1, page.Shown);
        Assert.Equal(1, page.Hidden);
        Assert.Contains("Body", page.Html);
        Assert.DoesNotContain("<script", page.Html);
        Assert.Contains("script-src 'none'", page.Html);
    }

    [Fact]
    public async Task Thread_with_nothing_readable_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<CompanionException>(() =>
            _service.GetThreadAsync(new ThreadRequest(new[] { new RefDto("gone", "store") })));

        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public void Inline_images_are_embedded_and_paths_outside_the_folder_are_not()
    {
        Directory.CreateDirectory(Path.Combine(_images, "m1"));
        File.WriteAllBytes(Path.Combine(_images, "m1", "logo.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "outside.png"), "secret");

        var html = "<img src=\"https://inline-images.example/m1/logo.png\">" +
                   "<img src=\"https://inline-images.example/..%2Foutside.png\">";

        var result = CompanionService.EmbedInlineImages(html, _images);

        Assert.Contains("data:image/png;base64,AQID", result);
        Assert.Contains("https://inline-images.example/..%2Foutside.png", result);
    }
}

public class CompanionNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.4.5.6", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.0.9", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_private_addresses_count_as_the_same_network(string address, bool expected)
    {
        Assert.Equal(expected, LocalNetwork.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public void Identity_survives_a_restart_and_a_reset_replaces_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"companion-{Guid.NewGuid():N}.key");
        try
        {
            var first = CompanionIdentity.LoadOrCreate(path);
            var again = CompanionIdentity.LoadOrCreate(path);

            Assert.Equal(first.Token, again.Token);
            Assert.Equal(first.Fingerprint, again.Fingerprint);
            Assert.Equal(64, first.Fingerprint.Length);
            Assert.True(first.Accepts(again.Token));
            Assert.False(first.Accepts(first.Token + "x"));
            Assert.False(first.Accepts(null));

            var reset = CompanionIdentity.Create(path);
            Assert.NotEqual(first.Token, reset.Token);
            Assert.NotEqual(first.Fingerprint, reset.Fingerprint);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pairing_link_carries_everything_the_phone_needs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"companion-{Guid.NewGuid():N}.key");
        try
        {
            var identity = CompanionIdentity.LoadOrCreate(path);
            var link = Pairing.Link(new[] { IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.5") }, 47821, identity, "NATE-PC");

            var uri = new Uri(link);
            Assert.Equal("emailtriage", uri.Scheme);
            Assert.Contains("h=192.168.1.20%2C10.0.0.5", link);
            Assert.Contains("p=47821", link);
            Assert.Contains($"f={identity.Fingerprint}", link);
            Assert.Contains("n=NATE-PC", link);

            var png = Pairing.QrPng(link);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));
        }
        finally { File.Delete(path); }
    }
}

/// <summary>The real server, over real TLS on loopback, with the phone's pinning rule.</summary>
public class CompanionServerTests : IAsyncLifetime
{
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"companion-{Guid.NewGuid():N}.key");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");
    private CompanionIdentity _identity = null!;
    private CompanionServer _server = null!;
    private Database _db = null!;

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

        _server = new CompanionServer(service, _identity, FreePort());
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_keyPath); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private HttpClient Client(string? token, string? pin = null)
    {
        pin ??= _identity.Fingerprint;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(cert.RawData)).Equals(pin, StringComparison.OrdinalIgnoreCase),
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_server.Port}") };
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Paired_phone_is_answered()
    {
        using var client = Client(_identity.Token);

        var hello = await client.GetFromJsonAsync<HelloDto>("/api/hello");

        Assert.Equal("TEST-PC", hello!.PcName);
    }

    [Fact]
    public async Task Request_without_the_token_is_refused()
    {
        using var client = Client(token: null);

        var response = await client.GetAsync("/api/inbox");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("isn't paired", (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
    }

    [Fact]
    public async Task Wrong_token_is_refused()
    {
        using var client = Client("not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/hello")).StatusCode);
    }

    [Fact]
    public async Task Refusals_from_the_service_come_back_as_their_status()
    {
        using var client = Client(_identity.Token);

        var response = await client.PostAsJsonAsync("/api/archive", new RefsRequest(Array.Empty<RefDto>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("No messages given.", (await response.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
    }

    [Fact]
    public async Task Phone_pinning_another_certificate_does_not_connect()
    {
        using var client = Client(_identity.Token, pin: new string('0', 64));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/api/hello"));
    }
}
