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
                        var storeName = ComUtil.Str(() => store!.DisplayName);

                        // Public folders and shared archives can be enormous and
                        // slow to enumerate; skip anything not cached locally.
                        root = ComUtil.Try<object?>(() => store!.GetRootFolder());
                        if (root is null) continue;

                        WalkFolders((object)root!, storeName, 0, nodes, ct);
                    }
                    catch
                    {
                        // A store that will not open (offline archive, bad
                        // credentials) should not sink the whole index.
                    }
                    finally { ComUtil.ReleaseAll(root, store); }
                }
            }
            finally { ComUtil.Release(stores); }

            return nodes;
        }, ct);

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
                            ItemCount = ComUtil.Int(() => child!.Items.Count),
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
            ItemCount = ComUtil.Int(() => folder.Items.Count),
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
