using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace EmailTriage.Outlook;

/// <summary>
/// Outlook's ItemsEvents dispinterface. Declared by hand because the rest of
/// the store is late bound and carries no interop assembly; the GUID and
/// DISPIDs are fixed by Outlook's type library.
/// </summary>
[ComVisible(true)]
[Guid("00063077-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IOutlookItemsEvents
{
    [DispId(0xF001)] void ItemAdd([MarshalAs(UnmanagedType.IDispatch)] object item);
    [DispId(0xF002)] void ItemChange([MarshalAs(UnmanagedType.IDispatch)] object item);
    [DispId(0xF003)] void ItemRemove();
}

/// <summary>Receives Inbox events from Outlook and forwards a bare "something changed".</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class OutlookItemsEventSink : IOutlookItemsEvents
{
    private readonly Action _changed;

    public OutlookItemsEventSink(Action changed) => _changed = changed;

    // The item arguments are released straight away: the list re-reads the
    // folder anyway, and holding them would keep Outlook items alive.
    public void ItemAdd(object item) { ComUtil.Release(item); _changed(); }
    public void ItemChange(object item) { ComUtil.Release(item); _changed(); }
    public void ItemRemove() => _changed();
}

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    /// <summary>
    /// How long to wait for a burst of events to settle before telling the app.
    /// A sync, a rule run or our own move can fire several events at once; one
    /// refresh after the last of them is enough.
    /// </summary>
    private static readonly TimeSpan ChangeSettle = TimeSpan.FromMilliseconds(250);

    // Only touched on the dispatcher thread. Each Items object must be held for
    // as long as we want its events: Outlook stops raising them once released.
    private readonly List<(object Items, IConnectionPoint Point, int Cookie, OutlookItemsEventSink Sink)> _watches = new();

    private Timer? _changeSettle;

    /// <summary>
    /// Subscribes to add/change/remove in the Inbox, and in Sent Items so a
    /// follow-up sent from Outlook lifts its conversation at once. Called on
    /// the dispatcher thread, whose message pump is what delivers the events.
    /// A failure here is not fatal: the poll still notices Inbox changes.
    /// </summary>
    private void StartWatching()
    {
        StopWatching();
        Watch(ComUtil.FolderInbox);
        Watch(FolderSentMail);
    }

    private void Watch(int defaultFolder)
    {
        dynamic? folder = null;
        object? items = null;
        try
        {
            folder = _session!.GetDefaultFolder(defaultFolder);
            items = folder!.Items;

            var iid = typeof(IOutlookItemsEvents).GUID;
            ((IConnectionPointContainer)items!).FindConnectionPoint(ref iid, out var found);
            var point = found ?? throw new InvalidOperationException("Outlook offered no item events.");

            var sink = new OutlookItemsEventSink(SignalInboxChanged);
            point.Advise(sink, out var cookie);
            _watches.Add((items, point, cookie, sink));
            items = null; // held by _watches now
        }
        catch { /* this folder just is not live */ }
        finally { ComUtil.ReleaseAll(items, folder); }
    }

    private void StopWatching()
    {
        foreach (var (items, point, cookie, _) in _watches)
        {
            try { point.Unadvise(cookie); } catch { /* Outlook already gone */ }
            ComUtil.ReleaseAll(point, items);
        }
        _watches.Clear();
    }

    /// <summary>Restarts the settle timer; the event fires once things go quiet.</summary>
    private void SignalInboxChanged()
    {
        var timer = _changeSettle ??= new Timer(
            _ => InboxChanged?.Invoke(this, EventArgs.Empty), null,
            Timeout.Infinite, Timeout.Infinite);

        try { timer.Change(ChangeSettle, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }
}
