using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class LruCacheTests
{
    [Fact]
    public void Evicts_the_least_recently_used_entry()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.TryGet("a", out _); // a is now fresher than b
        cache.Set("c", 3);

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void GetOrAdd_creates_once()
    {
        var cache = new LruCache<string, int>(4);
        var calls = 0;

        Assert.Equal(7, cache.GetOrAdd("k", _ => { calls++; return 7; }));
        Assert.Equal(7, cache.GetOrAdd("k", _ => { calls++; return 9; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Set_replaces_and_Remove_forgets()
    {
        var cache = new LruCache<string, int>(4);
        cache.Set("k", 1);
        cache.Set("k", 2);
        Assert.True(cache.TryGet("k", out var v));
        Assert.Equal(2, v);
        Assert.Equal(1, cache.Count);

        cache.Remove("k");
        Assert.False(cache.TryGet("k", out _));
    }
}
