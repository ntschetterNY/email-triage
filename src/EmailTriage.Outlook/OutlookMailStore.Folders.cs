using System.Runtime.Versioning;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    /// <summary>Guards against pathological or looping folder structures.</summary>
    private const int MaxFolderDepth = 12;

    /// <summary>Store.ExchangeStoreType for public folders: never mail move targets.</summary>
    private const int ExchangePublicFolderStore = 2;

    /// <summary>Store.ExchangeStoreType for a PST or other non-Exchange store.</summary>
    private const int NotExchangeStore = 3;

    private sealed record StoreInfo(int Index, string Name, string StoreId, string RootPath, int TopLevelCount);

    /// <summary>
    /// Builds the flat folder index. Every property read is a cross-process
    /// round trip to outlook.exe, and a real mailbox runs to hundreds of
    /// folders, so this is the slowest thing the app does. Two consequences:
    /// the walk reads as few properties per folder as it can, and it is split
    /// into one dispatcher job per top-level folder so a move or create the
    /// user triggers meanwhile is not stuck behind the whole walk.
    /// </summary>
    public async Task<IReadOnlyList<FolderNode>> GetFolderIndexAsync(CancellationToken ct = default)
    {
        var stores = await _sta.InvokeAsync(ListStores, ct).ConfigureAwait(false);
        var nodes = new List<FolderNode>(512);

        foreach (var store in stores)
        {
            for (int i = 1; i <= store.TopLevelCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var top = i;
                var chunk = await _sta.InvokeAsync(() => WalkTopLevel(store, top, ct), ct)
                    .ConfigureAwait(false);
                nodes.AddRange(chunk);
            }
        }

        return nodes;
    }

    /// <summary>Stores worth indexing, the default mailbox first.</summary>
    private IReadOnlyList<StoreInfo> ListStores()
    {
        EnsureConnected();

        var result = new List<StoreInfo>();
        dynamic? stores = null, defaultStore = null;
        try
        {
            defaultStore = ComUtil.Try<object?>(() => _session!.DefaultStore);
            var defaultId = defaultStore is null ? "" : ComUtil.Str(() => defaultStore!.StoreID);

            stores = _session!.Stores;
            int storeCount = ComUtil.Int(() => stores!.Count);

            for (int i = 1; i <= storeCount; i++)
            {
                dynamic? store = null, root = null, folders = null;
                try
                {
                    store = stores![i];
                    var storeId = ComUtil.Str(() => store!.StoreID);
                    var isDefault = storeId.Length > 0 && storeId == defaultId;
                    var exchangeType = ComUtil.Int(() => store!.ExchangeStoreType, -1);

                    if (exchangeType == ExchangePublicFolderStore) continue;

                    // Exchange stores that are not cached locally (the Online
                    // Archive, most shared mailboxes) are read over the network
                    // folder by folder: minutes for a big one. An Online Archive
                    // also mirrors the mailbox's folder names, so indexing it
                    // doubles every search hit. PSTs and the default mailbox
                    // are always kept.
                    if (!isDefault && exchangeType != NotExchangeStore &&
                        !ComUtil.Bool(() => store!.IsCachedExchange)) continue;

                    root = ComUtil.Try<object?>(() => store!.GetRootFolder());
                    if (root is null) continue;

                    folders = root!.Folders;
                    result.Add(new StoreInfo(
                        i,
                        ComUtil.Str(() => store!.DisplayName),
                        storeId,
                        ComUtil.Str(() => root!.FolderPath).TrimStart('\\'),
                        ComUtil.Int(() => folders!.Count)));
                }
                catch
                {
                    // A store that will not open (offline archive, bad
                    // credentials) should not sink the whole index.
                }
                finally { ComUtil.ReleaseAll(folders, root, store); }
            }

            return result
                .OrderByDescending(s => s.StoreId.Length > 0 && s.StoreId == defaultId)
                .ToList();
        }
        finally { ComUtil.ReleaseAll(stores, defaultStore); }
    }

    private List<FolderNode> WalkTopLevel(StoreInfo store, int index, CancellationToken ct)
    {
        EnsureConnected();

        var nodes = new List<FolderNode>();
        dynamic? stores = null, s = null, root = null, folders = null, top = null;
        try
        {
            stores = _session!.Stores;
            s = stores![store.Index];
            root = s!.GetRootFolder();
            folders = root!.Folders;
            top = folders![index];

            AddAndDescend((object)top!, store, store.RootPath, 0, nodes, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* skip an unreadable branch, keep the rest */ }
        finally { ComUtil.ReleaseAll(top, folders, root, s, stores); }

        return nodes;
    }

    private static void AddAndDescend(
        object folderObj, StoreInfo store, string parentPath, int depth,
        List<FolderNode> into, CancellationToken ct)
    {
        dynamic folder = folderObj;
        if (depth > MaxFolderDepth) return;
        ct.ThrowIfCancellationRequested();

        // Path and store id are derived rather than read: each read is a
        // round trip, and FolderPath is just the parent's path plus the name.
        var name = ComUtil.Str(() => folder.Name);
        var path = parentPath.Length == 0 ? name : parentPath + "\\" + name;

        // Only folders that hold mail are move targets.
        if (ComUtil.Int(() => folder.DefaultItemType, -1) == ComUtil.DefaultItemTypeMail)
        {
            into.Add(new FolderNode
            {
                Ref = new FolderRef(ComUtil.Str(() => folder.EntryID), store.StoreId, "\\\\" + path),
                Name = name,
                Path = path,
                Depth = depth,
                StoreName = store.Name,
            });
        }

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
                    AddAndDescend((object)child!, store, path, depth + 1, into, ct);
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
        _sta.InvokeAsync(() =>
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
        _sta.InvokeAsync(() =>
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
        _sta.InvokeAsync(() =>
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
        _sta.InvokeAsync(() =>
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
        _sta.InvokeAsync(() =>
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

            dynamic? folder = null, items = null, found = null;
            try
            {
                folder = searchFolder is { IsEmpty: false } f
                    ? _session!.GetFolderFromID(f.EntryId, f.StoreId)
                    : _session!.GetDefaultFolder(ComUtil.FolderInbox);

                items = folder!.Items;

                var filter =
                    $"@SQL=\"{ComUtil.PropInternetMessageId}\" = '{ComUtil.EscapeSql(internetMessageId)}'";

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
        }, ct);
}
