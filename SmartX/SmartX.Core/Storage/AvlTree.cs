namespace SmartX.Core.Storage;

/// <summary>Cost of one range query, so the UI can show how little of the tree was touched.</summary>
public sealed record RangeStats(int NodesVisited, int Matched, int TotalStored, int TreeHeight);

/// <summary>
/// Self-balancing AVL tree (a height-balanced binary search tree). After every insert
/// or delete each node's left and right subtrees differ in height by at most one, using
/// single and double rotations, so the tree height stays about 1.44 * log2(n) however
/// the keys arrive. That guarantees O(log n) insert, delete and lookup, and a range query
/// costs O(log n + k) for k matches: it descends to the lower bound, walks in order, and
/// stops past the upper bound, so it never touches the rest of the history.
/// Keys must be unique; inserting an existing key replaces its value.
/// </summary>
public sealed class AvlTree<TKey, TValue> where TKey : IComparable<TKey>
{
    private sealed class Node(TKey key, TValue value)
    {
        public TKey Key = key;
        public TValue Value = value;
        public int Height = 1;
        public Node? Left;
        public Node? Right;
    }

    private Node? _root;

    public int Count { get; private set; }

    public int Height => HeightOf(_root);

    public bool ContainsKey(TKey key) => Find(key) is not null;

    public bool TryGetValue(TKey key, out TValue value)
    {
        var node = Find(key);
        value = node is null ? default! : node.Value;
        return node is not null;
    }

    public void Set(TKey key, TValue value) => _root = Insert(_root, key, value);

    public bool Remove(TKey key)
    {
        var before = Count;
        _root = Delete(_root, key);
        return Count < before;
    }

    public bool TryGetMin(out TKey key, out TValue value)
    {
        if (_root is null)
        {
            key = default!;
            value = default!;
            return false;
        }

        var node = _root;
        while (node.Left is not null)
        {
            node = node.Left;
        }

        key = node.Key;
        value = node.Value;
        return true;
    }

    /// <summary>Every entry in ascending key order.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> InOrder()
    {
        var stack = new Stack<Node>();
        var current = _root;
        while (current is not null || stack.Count > 0)
        {
            while (current is not null)
            {
                stack.Push(current);
                current = current.Left;
            }

            current = stack.Pop();
            yield return new KeyValuePair<TKey, TValue>(current.Key, current.Value);
            current = current.Right;
        }
    }

    /// <summary>The newest <paramref name="count"/> entries, returned oldest first.</summary>
    public List<KeyValuePair<TKey, TValue>> Last(int count)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        var stack = new Stack<Node>();
        var current = _root;
        while ((current is not null || stack.Count > 0) && result.Count < count)
        {
            while (current is not null)
            {
                stack.Push(current);
                current = current.Right;
            }

            current = stack.Pop();
            result.Add(new KeyValuePair<TKey, TValue>(current.Key, current.Value));
            current = current.Left;
        }

        result.Reverse();
        return result;
    }

    /// <summary>All entries with from &lt;= key &lt;= to, ascending, plus how many nodes were visited.</summary>
    public (List<KeyValuePair<TKey, TValue>> Items, RangeStats Stats) Range(TKey from, TKey to)
    {
        var items = new List<KeyValuePair<TKey, TValue>>();
        var visited = 0;
        CollectRange(_root, from, to, items, ref visited);
        return (items, new RangeStats(visited, items.Count, Count, Height));
    }

    private static void CollectRange(Node? node, TKey from, TKey to, List<KeyValuePair<TKey, TValue>> items, ref int visited)
    {
        if (node is null)
        {
            return;
        }

        visited++;
        var aboveLower = node.Key.CompareTo(from) >= 0;
        var belowUpper = node.Key.CompareTo(to) <= 0;

        // Only descend into a side if it can still contain keys inside the window.
        if (aboveLower)
        {
            CollectRange(node.Left, from, to, items, ref visited);
        }

        if (aboveLower && belowUpper)
        {
            items.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
        }

        if (belowUpper)
        {
            CollectRange(node.Right, from, to, items, ref visited);
        }
    }

    private Node? Find(TKey key)
    {
        var node = _root;
        while (node is not null)
        {
            var cmp = key.CompareTo(node.Key);
            if (cmp == 0)
            {
                return node;
            }

            node = cmp < 0 ? node.Left : node.Right;
        }

        return null;
    }

    private Node Insert(Node? node, TKey key, TValue value)
    {
        if (node is null)
        {
            Count++;
            return new Node(key, value);
        }

        var cmp = key.CompareTo(node.Key);
        if (cmp < 0)
        {
            node.Left = Insert(node.Left, key, value);
        }
        else if (cmp > 0)
        {
            node.Right = Insert(node.Right, key, value);
        }
        else
        {
            node.Value = value;
            return node;
        }

        return Rebalance(node);
    }

    private Node? Delete(Node? node, TKey key)
    {
        if (node is null)
        {
            return null;
        }

        var cmp = key.CompareTo(node.Key);
        if (cmp < 0)
        {
            node.Left = Delete(node.Left, key);
        }
        else if (cmp > 0)
        {
            node.Right = Delete(node.Right, key);
        }
        else
        {
            if (node.Left is null || node.Right is null)
            {
                Count--;
                return node.Left ?? node.Right;
            }

            // Two children: replace with the in-order successor, then delete that successor.
            var successor = node.Right;
            while (successor.Left is not null)
            {
                successor = successor.Left;
            }

            node.Key = successor.Key;
            node.Value = successor.Value;
            node.Right = Delete(node.Right, successor.Key);
        }

        return Rebalance(node);
    }

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static void Update(Node node) => node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));

    private static int BalanceOf(Node node) => HeightOf(node.Left) - HeightOf(node.Right);

    private static Node Rebalance(Node node)
    {
        Update(node);
        var balance = BalanceOf(node);

        if (balance > 1)
        {
            if (BalanceOf(node.Left!) < 0)
            {
                node.Left = RotateLeft(node.Left!);   // left-right case
            }

            return RotateRight(node);                  // left-left case
        }

        if (balance < -1)
        {
            if (BalanceOf(node.Right!) > 0)
            {
                node.Right = RotateRight(node.Right!); // right-left case
            }

            return RotateLeft(node);                   // right-right case
        }

        return node;
    }

    private static Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        Update(y);
        Update(x);
        return x;
    }

    private static Node RotateLeft(Node x)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        Update(x);
        Update(y);
        return y;
    }
}
