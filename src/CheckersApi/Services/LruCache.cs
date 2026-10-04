namespace CheckersApi.Services;

/// <summary>Thread-safe LRU cache with absolute TTL per entry.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new(); // front = most recently used
    private readonly object _gate = new();

    private sealed record Entry(TKey Key, TValue Value, DateTimeOffset Expires);

    public LruCache(int capacity, TimeSpan ttl, TimeProvider? time = null)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl;
        _time = time ?? TimeProvider.System;
    }

    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (node.Value.Expires > _time.GetUtcNow())
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                _order.Remove(node);
                _map.Remove(key);
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            var node = new LinkedListNode<Entry>(new Entry(key, value, _time.GetUtcNow() + _ttl));
            _order.AddFirst(node);
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
