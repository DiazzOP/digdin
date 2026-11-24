namespace RbxSwitcher.Core.Sessions;

/// <summary>
/// Decides which account browsers stay alive. The most recently used <c>capacity</c> keys stay;
/// <see cref="Touch"/> returns the keys to close. The active key is never evicted.
/// </summary>
public sealed class LivePool<TKey> where TKey : notnull
{
    private readonly LinkedList<TKey> _order = new(); // first = most recent
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes = new();
    private int _capacity;

    public LivePool(int capacity) => Capacity = capacity;

    public int Capacity
    {
        get => _capacity;
        set => _capacity = Math.Clamp(value, 1, 50);
    }

    public int Count => _nodes.Count;
    public bool Contains(TKey key) => _nodes.ContainsKey(key);
    public IEnumerable<TKey> Keys => _order;

    /// <summary>Marks <paramref name="key"/> as in use and returns what must be closed to stay within capacity.</summary>
    public IReadOnlyList<TKey> Touch(TKey key)
    {
        if (_nodes.TryGetValue(key, out var node))
            _order.Remove(node);
        _nodes[key] = _order.AddFirst(key);
        return Trim();
    }

    public IReadOnlyList<TKey> Trim()
    {
        var evicted = new List<TKey>();
        while (_nodes.Count > _capacity && _order.Last is { } last && !EqualityComparer<TKey>.Default.Equals(last.Value, _order.First!.Value))
        {
            _order.RemoveLast();
            _nodes.Remove(last.Value);
            evicted.Add(last.Value);
        }
        return evicted;
    }

    public void Remove(TKey key)
    {
        if (_nodes.Remove(key, out var node))
            _order.Remove(node);
    }

    public void Clear()
    {
        _order.Clear();
        _nodes.Clear();
    }
}
