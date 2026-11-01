// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Utility;

namespace Content.Client._NC.Meta.Editor;

/// <summary>Immutable, bounded editor snapshots. Construction never delegates empty splits to engine rebalancing.</summary>
public static class NCMetaEditorRope
{
    private const int LeafCharacters = 1024;

    public static Rope.Node Create(string text)
    {
        if (text.Length > NCMetaCodeEditor.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(text));
        if (text.Length == 0) return Rope.Leaf.Empty;
        var leaves = new List<Rope.Node>((text.Length + LeafCharacters - 1) / LeafCharacters);
        for (var offset = 0; offset < text.Length;)
        {
            var count = Math.Min(LeafCharacters, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
            leaves.Add(new Rope.Leaf(text.Substring(offset, count)));
            offset += count;
        }
        return Merge(leaves, 0, leaves.Count);
    }

    public static Rope.Node Replace(Rope.Node snapshot, int start, int length, string insertion)
    {
        var text = Rope.Collapse(snapshot);
        if (start < 0 || length < 0 || start > text.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        if ((long) text.Length - length + insertion.Length > NCMetaCodeEditor.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(insertion));
        return Create(string.Concat(text.AsSpan(0, start), insertion.AsSpan(), text.AsSpan(start + length)));
    }

    private static Rope.Node Merge(List<Rope.Node> leaves, int start, int count)
    {
        if (count == 1) return leaves[start];
        var left = count / 2;
        return new Rope.Branch(Merge(leaves, start, left), Merge(leaves, start + left, count - left));
    }
}
