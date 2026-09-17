namespace ClipboardPlus;

// Store serialized bytes, not decoded bitmaps or arbitrary object graphs. Account every cached byte.
public sealed class PayloadCache
{
    private readonly Dictionary<long, LinkedListNode<(long Id, byte[] Data)>> entries = [];
    private readonly LinkedList<(long Id, byte[] Data)> lru = [];
    public long Bytes { get; private set; }
    public long Budget { get; private set; }
    public PayloadCache(long budget) => Budget = budget;
    public void SetBudget(long budget) { Budget = budget; Trim(); }
    public byte[]? Get(long id)
    {
        if (!entries.TryGetValue(id, out var node)) return null;
        lru.Remove(node); lru.AddFirst(node); return node.Value.Data;
    }
    public void Add(long id, byte[] data)
    {
        Remove(id);
        if (data.LongLength > Budget) return;
        entries[id] = lru.AddFirst((id, data)); Bytes += data.LongLength; Trim();
    }
    public void Remove(long id)
    {
        if (!entries.Remove(id, out var node)) return;
        lru.Remove(node); Bytes -= node.Value.Data.LongLength;
    }
    private void Trim() { while (Bytes > Budget && lru.Last is { } node) Remove(node.Value.Id); }
    public void Clear() { entries.Clear(); lru.Clear(); Bytes = 0; }
}
