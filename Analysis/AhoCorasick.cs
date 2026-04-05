namespace BinaryCarver.Analysis;

/// <summary>
/// Aho-Corasick multi-pattern matching automaton.
/// Builds a trie from all magic byte patterns and scans the input in a single pass,
/// matching every pattern simultaneously. O(n + m + z) where n=input length,
/// m=total pattern length, z=number of matches.
///
/// This replaces the sequential per-signature FindPattern loop from the original
/// engine, which was O(n * k) for k signatures.
/// </summary>
public sealed class AhoCorasick
{
    // Trie node: children, failure link, and which patterns complete here
    private struct Node
    {
        public int[] Children;   // 256-entry jump table (byte value → node index, -1 = none)
        public int   Failure;    // failure/suffix link
        public int   Output;     // linked list head for output function (-1 = none)
    }

    // Output list entry: pattern index + link to next output at this node
    private struct OutputEntry
    {
        public int PatternIndex;
        public int Next;         // -1 = end of list
    }

    private readonly Node[]        _nodes;
    private readonly OutputEntry[] _outputs;
    private readonly int[]         _patternLengths;
    private int _nodeCount;
    private int _outputCount;

    /// <summary>
    /// Build the automaton from a set of byte patterns.
    /// Patterns may have different lengths. Duplicate patterns are handled correctly.
    /// </summary>
    public AhoCorasick(IReadOnlyList<byte[]> patterns)
    {
        _patternLengths = new int[patterns.Count];
        for (int i = 0; i < patterns.Count; i++)
            _patternLengths[i] = patterns[i].Length;

        // Worst-case node count: sum of all pattern lengths + 1 (root)
        int maxNodes = 1;
        foreach (var p in patterns) maxNodes += p.Length;

        _nodes   = new Node[maxNodes];
        _outputs = new OutputEntry[patterns.Count + maxNodes]; // generous

        _nodeCount  = 1;  // root = node 0
        _outputCount = 0;

        // Initialize root
        _nodes[0].Children = NewChildArray();
        _nodes[0].Failure  = 0;
        _nodes[0].Output   = -1;

        // Phase 1: Build trie from patterns
        for (int pi = 0; pi < patterns.Count; pi++)
        {
            int cur = 0;
            foreach (byte b in patterns[pi])
            {
                int next = _nodes[cur].Children[b];
                if (next < 0)
                {
                    next = _nodeCount++;
                    _nodes[next].Children = NewChildArray();
                    _nodes[next].Failure  = 0;
                    _nodes[next].Output   = -1;
                    _nodes[cur].Children[b] = next;
                }
                cur = next;
            }
            // Add pattern index to output list at this node
            AddOutput(cur, pi);
        }

        // Phase 2: Build failure links via BFS
        var queue = new Queue<int>();

        // Depth-1 nodes: failure → root
        for (int b = 0; b < 256; b++)
        {
            int child = _nodes[0].Children[b];
            if (child > 0)
            {
                _nodes[child].Failure = 0;
                queue.Enqueue(child);
            }
            else
            {
                _nodes[0].Children[b] = 0; // point back to root
            }
        }

        // BFS for deeper nodes
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            for (int b = 0; b < 256; b++)
            {
                int v = _nodes[u].Children[b];
                if (v > 0)
                {
                    // Walk failure chain to find longest proper suffix that's a prefix
                    int f = _nodes[u].Failure;
                    while (f != 0 && _nodes[f].Children[b] <= 0)
                        f = _nodes[f].Failure;
                    _nodes[v].Failure = _nodes[f].Children[b];
                    if (_nodes[v].Failure == v) _nodes[v].Failure = 0; // avoid self-loop

                    // Merge output lists: this node's outputs + failure node's outputs
                    MergeOutputs(v);

                    queue.Enqueue(v);
                }
                else
                {
                    // Shortcut: point missing transitions through failure chain
                    int f = _nodes[u].Failure;
                    _nodes[u].Children[b] = _nodes[f].Children[b];
                }
            }
        }
    }

    /// <summary>
    /// Scan data[start..start+length) and return all matches.
    /// Each match is (patternIndex, matchStartOffset).
    /// </summary>
    public List<(int PatternIndex, int Offset)> Search(byte[] data, int start = 0, int length = -1)
    {
        if (length < 0) length = data.Length - start;
        int end = start + length;
        var results = new List<(int, int)>();
        int state = 0;

        for (int i = start; i < end; i++)
        {
            state = _nodes[state].Children[data[i]];

            // Collect all patterns that end at position i
            int outIdx = _nodes[state].Output;
            while (outIdx >= 0)
            {
                int pi = _outputs[outIdx].PatternIndex;
                int matchStart = i - _patternLengths[pi] + 1;
                results.Add((pi, matchStart));
                outIdx = _outputs[outIdx].Next;
            }
        }

        return results;
    }

    // ── Internal helpers ────────────────────────────────────────────────

    private static int[] NewChildArray()
    {
        var arr = new int[256];
        Array.Fill(arr, -1);
        return arr;
    }

    private void AddOutput(int nodeIndex, int patternIndex)
    {
        int idx = _outputCount++;
        _outputs[idx].PatternIndex = patternIndex;
        _outputs[idx].Next = _nodes[nodeIndex].Output;
        _nodes[nodeIndex].Output = idx;
    }

    private void MergeOutputs(int nodeIndex)
    {
        int failOut = _nodes[_nodes[nodeIndex].Failure].Output;
        if (failOut < 0) return;

        // Append failure node's output list to this node's output list
        if (_nodes[nodeIndex].Output < 0)
        {
            _nodes[nodeIndex].Output = failOut;
        }
        else
        {
            // Walk to end of this node's output list
            int tail = _nodes[nodeIndex].Output;
            while (_outputs[tail].Next >= 0)
                tail = _outputs[tail].Next;
            _outputs[tail].Next = failOut;
        }
    }
}
