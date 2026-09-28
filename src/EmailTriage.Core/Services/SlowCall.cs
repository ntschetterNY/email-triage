namespace EmailTriage.Core.Services;

/// <summary>
/// Says so when an Outlook call is taking too long, without giving up on it.
///
/// Walking away would not help: every Outlook call waits its turn on one
/// thread, so a stuck call holds up everything behind it however long the
/// caller waits. The usual cause is a dialog Outlook is showing (a security
/// prompt, a password box) and once the user answers it the call finishes
/// and its result is still wanted.
/// </summary>
public static class SlowCall
{
    /// <summary>
    /// Awaits <paramref name="task"/>, calling <paramref name="onSlow"/> once if it
    /// hasn't finished after <paramref name="after"/>. Resumes on the caller's
    /// context, so <paramref name="onSlow"/> may touch view-model state.
    /// </summary>
    public static async Task<T> WarnIfSlow<T>(this Task<T> task, TimeSpan after, Action onSlow)
    {
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(after, timer.Token);

        if (await Task.WhenAny(task, delay).ConfigureAwait(true) == delay) onSlow();
        else timer.Cancel();

        return await task.ConfigureAwait(true);
    }

    public static Task WarnIfSlow(this Task task, TimeSpan after, Action onSlow) =>
        WarnIfSlow(Wrap(task), after, onSlow);

    private static async Task<bool> Wrap(Task task)
    {
        await task.ConfigureAwait(false);
        return true;
    }
}
