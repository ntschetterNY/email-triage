namespace EmailTriage.Core.Services;

/// <summary>
/// A small least-recently-used cache. Not thread-safe: the reading pane's
/// caches are only ever touched from the UI thread.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

    public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(comparer);
    }

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing)) _order.Remove(existing);

        var node = _order.AddFirst((key, value));
        _map[key] = node;

        while (_map.Count > _capacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    public void Remove(TKey key)
    {
        if (!_map.Remove(key, out var node)) return;
        _order.Remove(node);
    }

    /// <summary>Returns the cached value, or creates, caches and returns one.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        if (TryGet(key, out var value)) return value;
        value = create(key);
        Set(key, value);
        return value;
    }
}
