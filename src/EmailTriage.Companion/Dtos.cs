using EmailTriage.Core.Models;

namespace EmailTriage.Companion;

// What goes over the wire to the phone. Kept apart from the Core models so
// the API stays stable while those change; serialized camelCase.

/// <summary>A mail item: its EntryId and StoreId, as Outlook knows it.</summary>
public sealed record RefDto(string E, string S)
{
    public static RefDto From(MailRef r) => new(r.EntryId, r.StoreId);
    public MailRef ToRef() => new(E, S);
}

public sealed record ConversationDto
{
    public required string Key { get; init; }
    public required string Subject { get; init; }

    /// <summary>Who wrote the newest message someone else sent.</summary>
    public required string From { get; init; }
    public required string FromAddress { get; init; }
    public required DateTimeOffset LastActivity { get; init; }
    public required bool Unread { get; init; }
    public required bool Attachments { get; init; }
    public required bool Flagged { get; init; }
    public required int Count { get; init; }

    /// <summary>"mail", or the meeting kind ("meetingRequest", ...).</summary>
    public required string Kind { get; init; }

    /// <summary>Your own follow-up is the newest thing in the thread.</summary>
    public required bool LatestIsMine { get; init; }

    /// <summary>The message a reply answers.</summary>
    public required RefDto ReplyTo { get; init; }

    /// <summary>The Inbox side of the thread: what archive, move, snooze, read and flag act on.</summary>
    public required IReadOnlyList<RefDto> Inbox { get; init; }

    /// <summary>Every message to show when reading, newest first, sent ones included.</summary>
    public required IReadOnlyList<RefDto> Messages { get; init; }
}

public sealed record InboxDto(IReadOnlyList<ConversationDto> Conversations, DateTimeOffset AsOf);

public sealed record ThreadPageDto(string Html, int Shown, int Hidden);

public sealed record FolderDto(string E, string S, string Path, string Name, string Breadcrumb);

public sealed record SnoozeOptionDto(string Label, string Hint, DateTimeOffset When);

public sealed record HelloDto(string PcName, string Version, string ActionCategory);

public sealed record ErrorDto(string Error);

public sealed record RefsRequest(IReadOnlyList<RefDto> Refs);

public sealed record ThreadRequest(IReadOnlyList<RefDto> Refs, bool Dark = true);

public sealed record MoveRequest(IReadOnlyList<RefDto> Refs, FolderDto Folder);

public sealed record SnoozeRequest(IReadOnlyList<RefDto> Refs, DateTimeOffset When);

public sealed record ReadRequest(IReadOnlyList<RefDto> Refs, bool Read);

public sealed record FlagRequest(IReadOnlyList<RefDto> Refs, bool On);

/// <param name="Scope">"all" (reply all) or "sender".</param>
/// <param name="ArchiveRefs">When set, these are archived once the reply has gone.</param>
public sealed record ReplyRequest(RefDto To, string Scope, string Text, IReadOnlyList<RefDto>? ArchiveRefs = null);

/// <summary>A request the API refuses, with the status to answer and a message to show.</summary>
public sealed class CompanionException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
