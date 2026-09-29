using System.Runtime.Versioning;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    /// <summary>Guards against pathological or looping folder structures.</summary>
    private const int MaxFolderDepth = 12;

    public Task<IReadOnlyList<FolderNode>> GetFolderIndexAsync(CancellationToken ct = default) =>
        _sta.InvokeAsync<IReadOnlyList<FolderNode>>(() =>
        {
            EnsureConnected();
            return MailFolders(ct);
        }, ct);

    /// <summary>
    /// The primary mailbox always counts, whatever its cache mode; .pst files
    /// are local by nature; any other Exchange store only when it is cached.
    /// </summary>
    private static bool IsLocallyAvailable(object storeObj)
    {
        dynamic store = storeObj;
        return ComUtil.Int(() => store.ExchangeStoreType, ComUtil.ExchangeStoreNotExchange) switch
        {
            ComUtil.ExchangeStorePrimaryMailbox => true,
            ComUtil.ExchangeStoreNotExchange => true,
            ComUtil.ExchangeStorePublicFolder => false,
            _ => ComUtil.Try(() => (bool)store.IsCachedExchange),
        };
    }

    private static void WalkFolders(
        object folderObj, string storeName, int depth, List<FolderNode> into, CancellationToken ct)
    {
        dynamic folder = folderObj;
        if (depth > MaxFolderDepth) return;
        ct.ThrowIfCancellationRequested();

        dynamic? children = null;
        try
        {
            children = folder.Folders;
            int count = ComUtil.Int(() => children!.Count);

            for (int i = 1; i <= count; i++)
            {
                dynamic? child = null;
                try
                {
                    child = children![i];

                    // Only folders that hold mail are move targets.
                    if (ComUtil.Int(() => child!.DefaultItemType, -1) == ComUtil.DefaultItemTypeMail)
                    {
                        into.Add(new FolderNode
                        {
                            Ref = ToFolderRef((object)child!),
                            Name = ComUtil.Str(() => child!.Name),
                            Path = ComUtil.Str(() => child!.FolderPath).TrimStart('\\'),
                            Depth = depth,
                            StoreName = storeName,
                        });
                    }

                    WalkFolders((object)child!, storeName, depth + 1, into, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip an unreadable branch, keep the rest */ }
                finally { ComUtil.Release(child); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally { ComUtil.Release(children); }
    }

    public Task<FolderNode> CreateFolderAsync(
        FolderRef parent, string name, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Folder name cannot be empty.", nameof(name));

            // Outlook rejects these outright, with an unhelpful message.
            if (name.IndexOfAny(new[] { '\\', '/', ':', '[', ']' }) >= 0)
                throw new ArgumentException(
                    @"A folder name cannot contain \ / : [ or ]", nameof(name));

            dynamic? parentFolder = null, folders = null, created = null;
            try
            {
                parentFolder = parent.IsEmpty
                    ? _session!.GetDefaultFolder(ComUtil.FolderInbox).Parent
                    : _session!.GetFolderFromID(parent.EntryId, parent.StoreId);

                folders = parentFolder!.Folders;

                // Reuse an existing folder of that name rather than failing.
                dynamic? existing = ComUtil.Try<object?>(() => folders![name]);
                if (existing is not null)
                {
                    try { return ToNode((object)existing!, ComUtil.Str(() => parentFolder!.Store.DisplayName)); }
                    finally { ComUtil.Release(existing); }
                }

                created = folders.Add(name);
                return ToNode((object)created!, ComUtil.Str(() => parentFolder!.Store.DisplayName));
            }
            finally { ComUtil.ReleaseAll(created, folders, parentFolder); }
        }, ct);

    /// <summary>olNormalWindow / olMinimized, as Explorer.WindowState reports them.</summary>
    private const int OlNormalWindow = 2;
    private const int OlMinimized = 1;

    public Task ShowFolderAsync(FolderRef folder, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            dynamic? target = null, explorer = null;
            try
            {
                target = _session!.GetFolderFromID(folder.EntryId, folder.StoreId);

                // Switch the window already open rather than stacking new ones.
                explorer = ComUtil.Try<object?>(() => _app!.ActiveExplorer());
                if (explorer is null)
                {
                    target!.Display();
                    return;
                }

                explorer.CurrentFolder = target;
                if (ComUtil.Int(() => explorer!.WindowState) == OlMinimized)
                    explorer.WindowState = OlNormalWindow;
                explorer.Activate();
            }
            finally { ComUtil.ReleaseAll(explorer, target); }
        }, ct);

    public Task<FolderNode> MoveFolderAsync(
        FolderRef folder, string targetPath, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            var segments = targetPath.Split(
                new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
                throw new ArgumentException("No folder to move to.", nameof(targetPath));
            if (segments.Any(s => s.IndexOfAny(new[] { ':', '[', ']' }) >= 0))
                throw new ArgumentException("A folder name cannot contain : [ or ]", nameof(targetPath));

            dynamic? source = null, store = null, parent = null, existing = null;
            try
            {
                source = _session!.GetFolderFromID(folder.EntryId, folder.StoreId);
                var sourceId = ComUtil.Str(() => source!.EntryID);
                store = source!.Store;
                var storeName = ComUtil.Str(() => store!.DisplayName);

                if (IsUnderDeletedItems((object)source!, (object)store!))
                    throw new InvalidOperationException("It is in Deleted Items.");

                // Walk down from the top of the folder's own mailbox, making
                // whatever is missing, to the folder it will sit in.
                parent = store!.GetRootFolder();
                foreach (var segment in segments[..^1])
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic? folders = null, next = null;
                    try
                    {
                        folders = parent!.Folders;
                        next = ComUtil.Try<object?>(() => folders![segment]) ?? folders.Add(segment);

                        if (ComUtil.Str(() => next!.EntryID) == sourceId)
                            throw new InvalidOperationException("A folder cannot be moved inside itself.");

                        ComUtil.Release(parent);
                        parent = next;
                        next = null;
                    }
                    finally { ComUtil.ReleaseAll(next, folders); }
                }

                var leaf = segments[^1];
                existing = ComUtil.Try<object?>(() => parent!.Folders[leaf]);

                if (existing is not null && ComUtil.Str(() => existing!.EntryID) != sourceId)
                {
                    MergeInto((object)source!, (object)existing!, ct);

                    // Only an emptied original is thrown away; anything that
                    // would not move stays put, where the user can see it.
                    if (IsEmpty((object)source!)) source!.Delete();
                    else throw new InvalidOperationException(
                        $"Merged into {ComUtil.Str(() => existing!.FolderPath).TrimStart('\\')}, but some items would not move and were left in the original.");

                    return ToNode((object)existing!, storeName);
                }

                var parentId = ComUtil.Str(() => parent!.EntryID);
                var currentParentId = ComUtil.Str(() => source!.Parent.EntryID);
                if (parentId != currentParentId) source!.MoveTo(parent);

                // Re-read it: a moved folder keeps its EntryID within a mailbox,
                // and the old reference may not follow the move.
                ComUtil.Release(source);
                source = _session!.GetFolderFromID(folder.EntryId, folder.StoreId);

                if (!string.Equals(ComUtil.Str(() => source!.Name), leaf, StringComparison.Ordinal))
                    source!.Name = leaf;

                return ToNode((object)source!, storeName);
            }
            finally { ComUtil.ReleaseAll(existing, parent, store, source); }
        }, ct);

    /// <summary>
    /// Moves every item and subfolder of <paramref name="fromObj"/> into
    /// <paramref name="intoObj"/>, merging subfolders that share a name.
    /// </summary>
    private void MergeInto(object fromObj, object intoObj, CancellationToken ct)
    {
        dynamic from = fromObj, into = intoObj;

        dynamic? items = null;
        try
        {
            items = from.Items;

            // Backwards: each move shortens the collection.
            for (int i = ComUtil.Int(() => items!.Count); i >= 1; i--)
            {
                ct.ThrowIfCancellationRequested();
                dynamic? item = null, moved = null;
                try
                {
                    item = items![i];
                    moved = item!.Move(into);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* left behind; the caller sees the folder is not empty */ }
                finally { ComUtil.ReleaseAll(moved, item); }
            }
        }
        finally { ComUtil.Release(items); }

        dynamic? subs = null, targets = null;
        try
        {
            subs = from.Folders;
            targets = into.Folders;
            for (int i = ComUtil.Int(() => subs!.Count); i >= 1; i--)
            {
                ct.ThrowIfCancellationRequested();
                dynamic? sub = null, twin = null;
                try
                {
                    sub = subs![i];
                    var name = ComUtil.Str(() => sub!.Name);
                    twin = ComUtil.Try<object?>(() => targets![name]);

                    if (twin is null)
                    {
                        sub!.MoveTo(into);
                    }
                    else
                    {
                        MergeInto((object)sub!, (object)twin!, ct);
                        if (IsEmpty((object)sub!)) sub!.Delete();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* left behind */ }
                finally { ComUtil.ReleaseAll(twin, sub); }
            }
        }
        finally { ComUtil.ReleaseAll(targets, subs); }
    }

    private static bool IsEmpty(object folderObj)
    {
        dynamic folder = folderObj;
        return ComUtil.Int(() => folder.Items.Count, 1) == 0
            && ComUtil.Int(() => folder.Folders.Count, 1) == 0;
    }

    private static bool IsUnderDeletedItems(object folderObj, object storeObj)
    {
        dynamic store = storeObj;
        var deleted = ComUtil.Str(() => store.GetDefaultFolder(FolderDeletedItems).FolderPath);
        if (deleted.Length == 0) return false;

        dynamic folder = folderObj;
        var path = ComUtil.Str(() => folder.FolderPath);
        return path.StartsWith(deleted + "\\", StringComparison.OrdinalIgnoreCase)
            || path.Equals(deleted, StringComparison.OrdinalIgnoreCase);
    }

    private static FolderNode ToNode(object folderObj, string storeName)
    {
        dynamic folder = folderObj;
        var path = ComUtil.Str(() => folder.FolderPath).TrimStart('\\');
        return new FolderNode
        {
            Ref = ToFolderRef((object)folder),
            Name = ComUtil.Str(() => folder.Name),
            Path = path,
            Depth = Math.Max(0, path.Count(c => c == '\\') - 1),
            StoreName = storeName,
        };
    }

    public Task<FolderRef> EnsureFolderPathAsync(
        string relativePath, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            dynamic? current = null;
            try
            {
                // Anchor at the root of the default store, beside the Inbox.
                current = _session!.GetDefaultFolder(ComUtil.FolderInbox).Parent;

                foreach (var segment in relativePath.Split(
                             new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    dynamic? folders = null, next = null;
                    try
                    {
                        folders = current!.Folders;
                        next = ComUtil.Try<object?>(() => folders![segment]) ?? folders.Add(segment);

                        ComUtil.Release(current);
                        current = next;
                        next = null;
                    }
                    finally { ComUtil.ReleaseAll(next, folders); }
                }

                return ToFolderRef((object)current!);
            }
            finally { ComUtil.Release(current); }
        }, ct);

    public Task<MailRef> MoveAsync(MailRef mail, FolderRef target, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null, folder = null, moved = null;
            try
            {
                item = GetItem(mail);
                folder = _session!.GetFolderFromID(target.EntryId, target.StoreId);

                moved = item!.Move(folder);

                if (moved is null)
                    throw new InvalidOperationException("Outlook refused the move.");

                // The EntryId changes on move; the caller must adopt the new one.
                return new MailRef(
                    ComUtil.Str(() => moved!.EntryID),
                    ComUtil.Str(() => moved!.Parent.StoreID));
            }
            finally { ComUtil.ReleaseAll(moved, folder, item); }
        }, ct);

    public Task SetCategoryAsync(
        MailRef mail, string category, bool on, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null;
            try
            {
                item = GetItem(mail);

                var current = ComUtil.ParseCategories(ComUtil.Str(() => item!.Categories)).ToList();
                bool present = current.Contains(category, StringComparer.OrdinalIgnoreCase);

                if (on == present) return;

                if (on) current.Add(category);
                else current.RemoveAll(c => c.Equals(category, StringComparison.OrdinalIgnoreCase));

                item!.Categories = ComUtil.JoinCategories(current);
                item.Save();
            }
            finally { ComUtil.Release(item); }
        }, ct);

    public Task SetReadAsync(MailRef mail, bool read, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null;
            try
            {
                item = GetItem(mail);
                if (ComUtil.Bool(() => item!.UnRead) == !read) return;

                item!.UnRead = !read;
                item.Save();
            }
            finally { ComUtil.Release(item); }
        }, ct);

    public Task<MailRef?> FindByMessageIdAsync(
        string internetMessageId, FolderRef? searchFolder, CancellationToken ct = default) =>
        _sta.InvokeAsync<MailRef?>(() =>
        {
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(internetMessageId)) return null;

            var filter =
                $"@SQL=\"{ComUtil.PropInternetMessageId}\" = '{ComUtil.EscapeSql(internetMessageId)}'";

            if (searchFolder is { IsEmpty: false } f)
                return FindInFolder(() => _session!.GetFolderFromID(f.EntryId, f.StoreId), filter);

            // The Inbox first, as that is where nearly everything still is; then
            // every mail folder, so mail a rule or the user filed away is found too.
            var inInbox = FindInFolder(() => _session!.GetDefaultFolder(ComUtil.FolderInbox), filter);
            if (inInbox is not null) return inInbox;

            foreach (var node in MailFolders(ct))
            {
                ct.ThrowIfCancellationRequested();
                var hit = FindInFolder(() => _session!.GetFolderFromID(node.Ref.EntryId, node.Ref.StoreId), filter);
                if (hit is not null) return hit;
            }
            return null;
        }, ct);

    /// <summary>Every mail folder in the locally available stores, the same set the folder index shows.</summary>
    private List<FolderNode> MailFolders(CancellationToken ct)
    {
        var nodes = new List<FolderNode>(256);
        dynamic? stores = null;
        try
        {
            stores = _session!.Stores;
            int storeCount = ComUtil.Int(() => stores!.Count);
            for (int i = 1; i <= storeCount; i++)
            {
                dynamic? store = null, root = null;
                try
                {
                    store = stores![i];

                    // Public folders and shared archives can be enormous and
                    // slow to enumerate; skip anything not cached locally.
                    // Every folder in an online store is a round-trip to
                    // Exchange, and walking one kept the palette waiting
                    // for minutes.
                    if (!IsLocallyAvailable((object)store!)) continue;

                    root = ComUtil.Try<object?>(() => store!.GetRootFolder());
                    if (root is null) continue;

                    WalkFolders((object)root!, ComUtil.Str(() => store!.DisplayName), 0, nodes, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // A store that will not open (offline archive, bad
                    // credentials) should not sink the rest.
                }
                finally { ComUtil.ReleaseAll(root, store); }
            }
        }
        finally { ComUtil.Release(stores); }
        return nodes;
    }

    private static MailRef? FindInFolder(Func<object?> openFolder, string filter)
    {
        dynamic? folder = null, items = null, found = null;
        try
        {
            folder = openFolder();
            if (folder is null) return null;

            items = folder.Items;
            found = items!.Find(filter);
            if (found is null) return null;

            return new MailRef(
                ComUtil.Str(() => found!.EntryID),
                ComUtil.Str(() => found!.Parent.StoreID));
        }
        catch
        {
            return null;
        }
        finally { ComUtil.ReleaseAll(found, items, folder); }
    }
}
