namespace SmartX.Core.Collections;

/// <summary>
/// Array-backed binary min-heap. The parent of index i lives at (i - 1) / 2 and its
/// children at 2i + 1 and 2i + 2, so the smallest priority is always at index 0.
/// Enqueue and Dequeue are O(log n) (sift up / sift down), Peek is O(1).
/// Written by hand, instead of using PriorityQueue&lt;,&gt;, so the Prim's algorithm in
/// <see cref="Mesh.MeshGraph"/> demonstrably runs on a heap we own and can explain.
/// </summary>
public sealed class MinHeap<TElement, TPriority>(IComparer<TPriority>? comparer = null)
{
    private readonly List<(TElement Element, TPriority Priority)> _items = [];
    private readonly IComparer<TPriority> _comparer = comparer ?? Comparer<TPriority>.Default;

    public int Count => _items.Count;

    public void Enqueue(TElement element, TPriority priority)
    {
        _items.Add((element, priority));
        SiftUp(_items.Count - 1);
    }

    public TElement Peek()
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("The heap is empty.");
        }

        return _items[0].Element;
    }

    public bool TryDequeue(out TElement element, out TPriority priority)
    {
        if (_items.Count == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }

        (element, priority) = _items[0];
        var last = _items.Count - 1;
        _items[0] = _items[last];
        _items.RemoveAt(last);

        if (_items.Count > 0)
        {
            SiftDown(0);
        }

        return true;
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (_comparer.Compare(_items[index].Priority, _items[parent].Priority) >= 0)
            {
                break;
            }

            (_items[index], _items[parent]) = (_items[parent], _items[index]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        var count = _items.Count;
        while (true)
        {
            var left = 2 * index + 1;
            var right = left + 1;
            var smallest = index;

            if (left < count && _comparer.Compare(_items[left].Priority, _items[smallest].Priority) < 0)
            {
                smallest = left;
            }

            if (right < count && _comparer.Compare(_items[right].Priority, _items[smallest].Priority) < 0)
            {
                smallest = right;
            }

            if (smallest == index)
            {
                return;
            }

            (_items[index], _items[smallest]) = (_items[smallest], _items[index]);
            index = smallest;
        }
    }
}
