using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.Companion;

/// <summary>Settings the companion reads on every request, so edits on the PC apply at once.</summary>
public sealed record CompanionOptions
{
    public string ActionCategory { get; init; } = "Action Required";
    public string SnoozeFolder { get; init; } = "Snoozed";
    public int InboxPageSize { get; init; } = 250;
    public int SentPageSize { get; init; } = 200;
    public int ThreadMessageLimit { get; init; } = 12;
    public bool BlockRemoteImages { get; init; }
    public SnoozeDayShape DayShape { get; init; } = SnoozeDayShape.Default;

    /// <summary>Where the mail store saves embedded images; null when it saves none.</summary>
    public string? InlineImageFolder { get; init; }

    public string PcName { get; init; } = Environment.MachineName;
    public string Version { get; init; } = "";
}

/// <summary>
/// Triage from the phone: the same moves the desktop list makes, against the
/// same Outlook connection and the same local database, so a snooze set on
/// the phone comes back through the desktop's scheduler and a flag lands on
/// the desktop's action board.
/// </summary>
public sealed partial class CompanionService
{
    /// <summary>Most a single embedded image may add to a page, before base64.</summary>
    private const int MaxInlineImageBytes = 4 * 1024 * 1024;

    private readonly IMailStore _store;
    private readonly ISnoozeRepository _snoozes;
    private readonly IActionItemRepository _actions;
    private readonly FolderSearchService _folders;
    private readonly IClock _clock;
    private readonly Func<CompanionOptions> _options;

    /// <summary>
    /// The Inbox messages the phone was last sent, by EntryId: snooze and flag
    /// need a message's Message-ID and sender, and the phone only sends refs.
    /// </summary>
    private readonly ConcurrentDictionary<string, MailSummary> _known = new(StringComparer.Ordinal);

    public CompanionService(
        IMailStore store,
        ISnoozeRepository snoozes,
        IActionItemRepository actions,
        FolderSearchService folders,
        IClock clock,
        Func<CompanionOptions> options)
    {
        _store = store;
        _snoozes = snoozes;
        _actions = actions;
        _folders = folders;
        _clock = clock;
        _options = options;
    }

    public HelloDto Hello()
    {
        var o = _options();
        return new HelloDto(o.PcName, o.Version, o.ActionCategory);
    }

    public async Task<InboxDto> GetInboxAsync(CancellationToken ct = default)
    {
        var o = _options();
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var inbox = await _store.GetInboxAsync(ct).ConfigureAwait(false);
        var mail = await _store.GetMailAsync(inbox, Math.Max(1, o.InboxPageSize), ct).ConfigureAwait(false);

        // Your side of each thread is decoration; the Inbox still shows without it.
        IReadOnlyList<MailSummary> sent;
        try
        {
            var sentFolder = await _store.GetSentItemsAsync(ct).ConfigureAwait(false);
            sent = await _store.GetMailAsync(sentFolder, Math.Max(0, o.SentPageSize), ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { sent = Array.Empty<MailSummary>(); }

        _known.Clear();
        foreach (var m in mail) _known[m.Ref.EntryId] = m;

        var threads = ConversationGrouper.Group(mail, sent);
        var limit = Math.Max(1, o.ThreadMessageLimit);

        var conversations = threads.Select(t =>
        {
            var latest = t.LatestInbox;
            return new ConversationDto
            {
                Key = t.Key,
                Subject = latest.Subject,
                From = latest.DisplaySender,
                FromAddress = latest.SenderAddress,
                LastActivity = t.LastActivityUtc,
                Unread = t.IsUnread,
                Attachments = t.HasAttachments,
                Flagged = t.InboxMessages.Any(m => HasCategory(m, o.ActionCategory)),
                Count = t.Count,
                Kind = JsonNamingPolicy.CamelCase.ConvertName(latest.Kind.ToString()),
                LatestIsMine = t.LatestIsMine,
                ReplyTo = RefDto.From(latest.Ref),
                Inbox = t.InboxMessages.Select(m => RefDto.From(m.Ref)).ToList(),
                Messages = t.Messages.Take(limit).Select(m => RefDto.From(m.Ref)).ToList(),
            };
        }).ToList();

        return new InboxDto(conversations, _clock.UtcNow);
    }

    /// <summary>The conversation as one page, the newest message open, as the desktop reading pane shows it.</summary>
    public async Task<ThreadPageDto> GetThreadAsync(ThreadRequest request, CancellationToken ct = default)
    {
        var o = _options();
        var refs = Require(request.Refs).Take(Math.Max(1, o.ThreadMessageLimit)).ToList();
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var bodies = new List<MailBody>(refs.Count);
        Exception? first = null;
        foreach (var r in refs)
        {
            try { bodies.Add(await _store.GetBodyAsync(r.ToRef(), ct).ConfigureAwait(false)); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { first ??= ex; }
        }

        if (bodies.Count == 0)
            throw new CompanionException(404, $"That conversation could not be opened: {first?.Message ?? "nothing to show"}");

        var hidden = request.Refs.Count - bodies.Count;
        var html = HtmlPresenter.RenderThread(bodies, o.BlockRemoteImages, hidden, request.Dark);
        return new ThreadPageDto(EmbedInlineImages(html, o.InlineImageFolder), bodies.Count, hidden);
    }

    public async Task<int> ArchiveAsync(RefsRequest request, CancellationToken ct = default)
    {
        var refs = Require(request.Refs);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        // The Archive beside the Inbox, created if missing - as the desktop's e.
        var archive = await _store.EnsureFolderPathAsync("Archive", ct).ConfigureAwait(false);
        return await MoveAllAsync(refs, archive, ct).ConfigureAwait(false);
    }

    public async Task<int> MoveAsync(MoveRequest request, CancellationToken ct = default)
    {
        var refs = Require(request.Refs);
        if (request.Folder is null || string.IsNullOrEmpty(request.Folder.E))
            throw new CompanionException(400, "No folder to move to.");
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var target = new FolderRef(request.Folder.E, request.Folder.S, request.Folder.Path);
        var moved = await MoveAllAsync(refs, target, ct).ConfigureAwait(false);

        // Count it toward the folders the palette ranks first, as a desktop move does.
        await _folders.EnsureIndexedAsync(ct: ct).ConfigureAwait(false);
        var node = _folders.Search("", int.MaxValue).Select(m => m.Folder)
            .FirstOrDefault(f => f.Ref.EntryId == target.EntryId);
        if (node is not null)
        {
            try { await _folders.RecordUseAsync(node, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* ranking only */ }
        }

        return moved;
    }

    /// <summary>The folders matching what was typed; the most-used ones when nothing was.</summary>
    public async Task<IReadOnlyList<FolderDto>> SearchFoldersAsync(string? query, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _folders.EnsureIndexedAsync(ct: ct).ConfigureAwait(false);

        return _folders.Search(query ?? "", 40)
            .Select(m => new FolderDto(
                m.Folder.Ref.EntryId, m.Folder.Ref.StoreId, m.Folder.Path, m.Folder.Name, m.Folder.Breadcrumb))
            .ToList();
    }

    public IReadOnlyList<SnoozeOptionDto> SnoozeOptions() =>
        SnoozePresets.For(_clock.Now, _options().DayShape)
            .Select(p => new SnoozeOptionDto(p.Label, p.Hint, p.When))
            .ToList();

    /// <summary>"tomorrow 9am", "fri", "3d" - read as the desktop's snooze palette reads it.</summary>
    public SnoozeOptionDto? ParseSnooze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var now = _clock.Now;
        if (!NaturalDateParser.TryParse(text, now, out var when, _options().DayShape) || when <= now) return null;
        return new SnoozeOptionDto(when.ToString("dddd d MMM, HH:mm"), Humanise(when - now), when);
    }

    /// <summary>
    /// Parks each message in the snooze folder with its own entry, so the
    /// desktop's scheduler puts them all back in the Inbox together.
    /// </summary>
    public async Task<int> SnoozeAsync(SnoozeRequest request, CancellationToken ct = default)
    {
        var refs = Require(request.Refs);
        if (request.When <= _clock.UtcNow) throw new CompanionException(400, "That time has already passed.");
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var o = _options();
        var origin = await _store.GetInboxAsync(ct).ConfigureAwait(false);
        var holding = await _store.EnsureFolderPathAsync(o.SnoozeFolder, ct).ConfigureAwait(false);

        var parked = 0;
        Exception? first = null;
        foreach (var r in refs)
        {
            var summary = Known(r);
            try
            {
                var to = await _store.MoveAsync(r.ToRef(), holding, ct).ConfigureAwait(false);
                await _snoozes.AddAsync(new SnoozeEntry
                {
                    InternetMessageId = summary.InternetMessageId,
                    EntryId = to.EntryId,
                    StoreId = to.StoreId,
                    Subject = summary.Subject,
                    SenderName = summary.DisplaySender,
                    OriginFolderEntryId = origin.EntryId,
                    OriginFolderStoreId = origin.StoreId,
                    OriginFolderPath = origin.Path,
                    SnoozedUtc = _clock.UtcNow,
                    ReturnUtc = request.When.ToUniversalTime(),
                }, ct).ConfigureAwait(false);

                _known.TryRemove(r.E, out _);
                parked++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { first ??= ex; }
        }

        return Settled(parked, refs.Count, first, "snooze");
    }

    public async Task SetReadAsync(ReadRequest request, CancellationToken ct = default)
    {
        var refs = Require(request.Refs);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        foreach (var r in refs) await _store.SetReadAsync(r.ToRef(), request.Read, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// On: the newest message gets a card on the action board and the action
    /// category in Outlook. Off: every open card on the thread goes, with the category.
    /// </summary>
    public async Task SetFlagAsync(FlagRequest request, CancellationToken ct = default)
    {
        var refs = Require(request.Refs);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var category = _options().ActionCategory;

        if (request.On)
        {
            var summary = refs.Select(Known).OrderByDescending(m => m.ReceivedUtc).First();
            await _actions.UpsertAsync(new ActionItem
            {
                InternetMessageId = summary.InternetMessageId,
                EntryId = summary.Ref.EntryId,
                StoreId = summary.Ref.StoreId,
                Subject = summary.Subject,
                SenderName = summary.SenderName,
                SenderAddress = summary.SenderAddress,
                ReceivedUtc = summary.ReceivedUtc,
                CreatedUtc = _clock.UtcNow,
            }, ct).ConfigureAwait(false);

            await _store.SetCategoryAsync(summary.Ref, category, true, ct).ConfigureAwait(false);
            return;
        }

        foreach (var r in refs)
        {
            if (_known.TryGetValue(r.E, out var summary) && summary.InternetMessageId.Length > 0
                && await _actions.GetByMessageIdAsync(summary.InternetMessageId, ct).ConfigureAwait(false) is { IsComplete: false } card)
            {
                await _actions.DeleteAsync(card.Id, ct).ConfigureAwait(false);
            }

            await _store.SetCategoryAsync(r.ToRef(), category, false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the reply in Outlook - recipients, quoted history and
    /// signature as Outlook makes them - puts the text on top and sends it.
    /// </summary>
    public async Task ReplyAsync(ReplyRequest request, CancellationToken ct = default)
    {
        if (request.To is null || string.IsNullOrEmpty(request.To.E))
            throw new CompanionException(400, "Nothing to reply to.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new CompanionException(400, "The reply is empty.");

        var scope = request.Scope?.Trim().ToLowerInvariant() switch
        {
            "sender" => ReplyScope.SenderOnly,
            "all" or null or "" => ReplyScope.All,
            _ => throw new CompanionException(400, "Reply scope must be \"all\" or \"sender\"."),
        };

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var draft = await _store.BuildReplyAsync(request.To.ToRef(), scope, ct).ConfigureAwait(false);

        var html = ComposeHtml.Render(ComposeDocument.FromPlainText(request.Text.Trim())).Html;
        try
        {
            await _store.SendReplyAsync(draft.Ref, html, null, ct).ConfigureAwait(false);
        }
        catch
        {
            // Leave nothing half-written in Outlook.
            try { await _store.DiscardDraftAsync(draft.Ref, CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }

        if (request.ArchiveRefs is { Count: > 0 } archive)
            await ArchiveAsync(new RefsRequest(archive), ct).ConfigureAwait(false);
    }

    // ---- helpers ----------------------------------------------------------

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_store.IsConnected) return;
        try { await _store.ConnectAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new CompanionException(503, $"Outlook isn't available on the PC: {ex.Message}");
        }
    }

    private async Task<int> MoveAllAsync(IReadOnlyList<RefDto> refs, FolderRef target, CancellationToken ct)
    {
        var moved = 0;
        Exception? first = null;
        foreach (var r in refs)
        {
            try
            {
                await _store.MoveAsync(r.ToRef(), target, ct).ConfigureAwait(false);
                _known.TryRemove(r.E, out _);
                moved++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { first ??= ex; }
        }
        return Settled(moved, refs.Count, first, "move");
    }

    /// <summary>Some done is a success (the phone refreshes); none done is an error worth showing.</summary>
    private static int Settled(int done, int asked, Exception? first, string verb) =>
        done == 0 && first is not null
            ? throw new CompanionException(409, $"Could not {verb} that conversation: {first.Message}")
            : done;

    private MailSummary Known(RefDto r) =>
        _known.TryGetValue(r.E, out var m)
            ? m
            : throw new CompanionException(409, "That message has changed on the PC. Pull down to refresh and try again.");

    private static IReadOnlyList<RefDto> Require(IReadOnlyList<RefDto>? refs) =>
        refs is { Count: > 0 } && refs.All(r => r is not null && !string.IsNullOrEmpty(r.E))
            ? refs
            : throw new CompanionException(400, "No messages given.");

    private static bool HasCategory(MailSummary m, string category) =>
        m.Categories.Any(c => string.Equals(c.Trim(), category, StringComparison.OrdinalIgnoreCase));

    private static string Humanise(TimeSpan span)
    {
        if (span.TotalMinutes < 60) return $"in {(int)Math.Ceiling(span.TotalMinutes)} minutes";
        if (span.TotalHours < 24) return $"in {(int)span.TotalHours} hours";
        return $"in {(int)span.TotalDays} days";
    }

    /// <summary>
    /// The desktop serves embedded images from a private host mapped onto the
    /// image folder; the phone cannot reach that, so each one is written into
    /// the page as a data: URI instead. Paths that climb out of the folder,
    /// missing files and very large images are left as they are.
    /// </summary>
    public static string EmbedInlineImages(string html, string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !html.Contains(MailImages.InlineImageHost, StringComparison.Ordinal))
            return html;

        var root = Path.GetFullPath(folder);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        return InlineImageUrlRegex().Replace(html, m =>
        {
            try
            {
                var relative = Uri.UnescapeDataString(m.Groups["path"].Value).Replace('/', Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return m.Value;

                var file = new FileInfo(full);
                if (!file.Exists || file.Length > MaxInlineImageBytes) return m.Value;

                return $"data:{ImageType(full)};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return m.Value;
            }
        });
    }

    private static string ImageType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        _ => "image/jpeg",
    };

    [GeneratedRegex(@"https://inline-images\.example/(?<path>[^""'\s)>]+)")]
    private static partial Regex InlineImageUrlRegex();
}
