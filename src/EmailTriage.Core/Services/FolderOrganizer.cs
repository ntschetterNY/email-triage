using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>One folder the organizer would nest, and where.</summary>
public sealed record FolderMovePlan
{
    public required FolderNode Folder { get; init; }

    /// <summary>Where it is now, from the top of its mailbox: "Inbox\Elara - Field Reports - Rimkus".</summary>
    public required string From { get; init; }

    /// <summary>Where it goes, from the top of the same mailbox: "Inbox\Elara\Field Reports\Rimkus".</summary>
    public required string To { get; init; }

    /// <summary>A folder is already at <see cref="To"/>; the contents are merged into it.</summary>
    public bool MergesIntoExisting { get; init; }
}

/// <summary>
/// Works out which folders are named in the user's scheme but not yet
/// nested by it, and where each one belongs. Pure: the moving itself is
/// <see cref="Abstractions.IMailStore.MoveFolderAsync"/>.
/// </summary>
public static class FolderOrganizer
{
    /// <summary>
    /// Outlook's own folders, and anything under them, are never reorganised.
    /// Matched on the English names, as the folder index carries only paths.
    /// </summary>
    private static readonly HashSet<string> SystemRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Deleted Items", "Junk Email", "Junk E-mail", "Outbox", "Drafts", "Sent Items",
        "Sync Issues", "Conversation History", "RSS Feeds", "RSS Subscriptions", "Search Folders",
    };

    /// <param name="homeFolder">
    /// Where the tree is built, from the top of the mailbox ("Inbox",
    /// "Projects"). Empty builds it where each folder already is.
    /// </param>
    public static IReadOnlyList<FolderMovePlan> Plan(
        IReadOnlyList<FolderNode> index, FolderScheme scheme, string? homeFolder)
    {
        if (!scheme.IsValid) return Array.Empty<FolderMovePlan>();

        var home = NormalisePath(homeFolder);

        // Existing folders by store and path, to spot merges.
        var existing = new HashSet<string>(
            index.Select(f => Key(f.Ref.StoreId, WithoutStoreRoot(f.Path))),
            StringComparer.OrdinalIgnoreCase);

        var plans = new List<FolderMovePlan>();
        foreach (var folder in index)
        {
            var levels = scheme.ToLevels(folder.Name);
            if (levels is null) continue;

            var from = WithoutStoreRoot(folder.Path);
            if (from.Length == 0 || IsSystem(from)) continue;

            var parent = ParentOf(from);
            var baseline = home.Length > 0 ? home : parent;
            var to = baseline.Length > 0 ? $"{baseline}\\{string.Join('\\', levels)}" : string.Join('\\', levels);

            if (to.Equals(from, StringComparison.OrdinalIgnoreCase)) continue;

            // Already filed under its own nesting, e.g. a folder named after
            // the scheme sitting at Elara\Field Reports - leave it be.
            if (to.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) continue;

            plans.Add(new FolderMovePlan
            {
                Folder = folder,
                From = from,
                To = to,
                MergesIntoExisting = existing.Contains(Key(folder.Ref.StoreId, to)),
            });
        }

        // Deepest first: a scheme-named folder inside another is moved out
        // before its parent is, so every path planned is still where it was.
        return plans
            .OrderByDescending(p => p.From.Count(c => c == '\\'))
            .ThenBy(p => p.To, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Where a new folder typed in the scheme ("Elara - Field Reports - Rimkus")
    /// is created, from the top of the default mailbox; null when the name
    /// does not follow the scheme.
    /// </summary>
    public static IReadOnlyList<string>? NewFolderLevels(string typed, FolderScheme scheme, string? homeFolder)
    {
        var levels = scheme.ToLevels(typed);
        if (levels is null) return null;

        var home = NormalisePath(homeFolder);
        return home.Length == 0
            ? levels
            : home.Split('\\', StringSplitOptions.RemoveEmptyEntries).Concat(levels).ToList();
    }

    public static string NormalisePath(string? path) =>
        string.Join('\\', (path ?? "")
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>"someone@example.com\Inbox\X" to "Inbox\X": the store's own name is on every path.</summary>
    public static string WithoutStoreRoot(string path)
    {
        path = path.TrimStart('\\');
        var idx = path.IndexOf('\\');
        return idx < 0 ? "" : path[(idx + 1)..];
    }

    private static string ParentOf(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx < 0 ? "" : path[..idx];
    }

    private static bool IsSystem(string storeRelative)
    {
        var top = storeRelative.Split('\\')[0];
        return SystemRoots.Contains(top);
    }

    private static string Key(string storeId, string path) => $"{storeId}|{path}";
}
