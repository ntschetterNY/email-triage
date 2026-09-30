using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class SlowCallTests
{
    [Fact]
    public async Task A_quick_call_does_not_warn()
    {
        var warned = false;
        var result = await Task.FromResult(7).WarnIfSlow(TimeSpan.FromSeconds(5), () => warned = true);

        Assert.Equal(7, result);
        Assert.False(warned);
    }

    [Fact]
    public async Task A_slow_call_warns_once_and_still_returns_its_result()
    {
        var gate = new TaskCompletionSource<int>();
        var warnings = 0;

        var waiting = gate.Task.WarnIfSlow(TimeSpan.FromMilliseconds(20), () =>
        {
            warnings++;
            gate.SetResult(3); // the dialog was answered
        });

        Assert.Equal(3, await waiting);
        Assert.Equal(1, warnings);
    }

    [Fact]
    public async Task A_failure_comes_through_as_is()
    {
        var failing = Task.FromException(new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failing.WarnIfSlow(TimeSpan.FromSeconds(5), () => { }));
        Assert.Equal("boom", ex.Message);
    }
}
