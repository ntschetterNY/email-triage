namespace EmailTriage.Core.Models;

/// <summary>
/// One node of the flattened folder index the move palette searches over.
/// </summary>
public sealed record FolderNode
{
    public required FolderRef Ref { get; init; }

    /// <summary>Leaf name, e.g. "Invoices".</summary>
    public required string Name { get; init; }

    /// <summary>Full human path, e.g. "Mailbox - Nate\Clients\Acme\Invoices".</summary>
    public required string Path { get; init; }

    /// <summary>Depth from the store root; used to bias search toward shallow folders.</summary>
    public required int Depth { get; init; }

    /// <summary>Name of the owning store, so multi-account users can tell folders apart.</summary>
    public required string StoreName { get; init; }

    /// <summary>
    /// The folders above this one, nearest first, each after a "-^" pointing
    /// up a level: "Clients\Acme\Invoices" gives "-^ Acme -^ Clients". Nearest
    /// first so a long trail loses the mailbox root to the ellipsis, not the
    /// parent that tells two same-named folders apart. Empty at the top.
    /// </summary>
    public string ParentTrail
    {
        get
        {
            var parents = Path.Split('\\', StringSplitOptions.RemoveEmptyEntries)[..^1];
            return string.Join(" ", Enumerable.Reverse(parents).Select(p => $"-^ {p}"));
        }
    }
}
