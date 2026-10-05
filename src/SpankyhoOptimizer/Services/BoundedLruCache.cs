using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _lru = new();

    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        _capacity = Math.Max(4, capacity);
        _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(_capacity, comparer);
    }

    public int Count { get { lock (_sync) return _map.Count; } }
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (!_map.TryGetValue(key, out var node)) { value = default!; return false; }
            _lru.Remove(node); _lru.AddFirst(node); value = node.Value.Value; return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = (key, value); _lru.Remove(existing); _lru.AddFirst(existing); return;
            }
            var node = new LinkedListNode<(TKey, TValue)>((key, value));
            _lru.AddFirst(node); _map[key] = node;
            if (_map.Count <= _capacity) return;
            var last = _lru.Last; if (last == null) return;
            _lru.RemoveLast(); _map.Remove(last.Value.Key);
        }
    }
    public void Clear()
    {
        lock (_sync) { _map.Clear(); _lru.Clear(); }
    }
}
