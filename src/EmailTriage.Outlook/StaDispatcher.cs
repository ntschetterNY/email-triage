using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EmailTriage.Outlook;

/// <summary>
/// Runs every Outlook call on one dedicated single-threaded-apartment thread.
///
/// This is not optional. Outlook's object model is STA-bound: COM pointers
/// obtained on one thread are not valid on another, and calling from thread-pool
/// threads produces the intermittent RPC_E_WRONGTHREAD and 0x80010105 failures
/// that make naive Outlook tools flaky. Confining all of it to one thread trades
/// a little throughput for behaviour that is actually predictable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;

    public StaDispatcher(string name = "Outlook STA")
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsOnDispatcherThread => Thread.CurrentThread == _thread;

    private void Pump()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            // A short timeout lets us service the Windows message queue between
            // work items. Outlook is an out-of-process COM server, and an STA
            // that never pumps can stall cross-apartment calls.
            if (_queue.TryTake(out var work, 50))
            {
                try { work(); }
                catch { /* the Task carries the failure back to the caller */ }
            }
            else
            {
                DrainMessageQueue();
            }
        }
    }

    private static void DrainMessageQueue()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (ct.IsCancellationRequested)
        {
            tcs.SetCanceled(ct);
            return tcs.Task;
        }

        // Re-entrant calls would deadlock waiting on our own queue.
        if (IsOnDispatcherThread)
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }

        try
        {
            _queue.Add(() =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, _shutdown.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(StaDispatcher)));
        }

        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken ct = default)
        => InvokeAsync<object?>(() => { work(); return null; }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shutdown.Cancel();
        _queue.CompleteAdding();

        // Give in-flight Outlook calls a moment to unwind cleanly rather than
        // tearing down the apartment underneath them.
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // The thread is background, so a hung Outlook call cannot block exit.
        }

        _queue.Dispose();
        _shutdown.Dispose();
    }

    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage msg, IntPtr hWnd, uint filterMin, uint filterMax, uint flags);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage msg);
}
