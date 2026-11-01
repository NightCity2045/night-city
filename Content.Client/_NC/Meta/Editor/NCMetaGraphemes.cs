// SPDX-License-Identifier: MIT
// Copyright (c) .NET Foundation and Contributors
// Adapted UAX #29 Rev. 35 behavior from dotnet/runtime TextSegmentationUtility.cs.
// Source: https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Text/Unicode/TextSegmentationUtility.cs
// See NCMetaGraphemes.license. Property data is generated at build time, never accessed through reflection in the client.
using System.Collections.Generic;
using System.Text;

namespace Content.Client._NC.Meta.Editor;

/// <summary>Portable extended grapheme boundaries with the same rules as the project's .NET 10 StringInfo.</summary>
public static partial class NCMetaGraphemes
{
    private enum Break : byte { Other, CR, LF, Control, Extend, ZWJ, RegionalIndicator, Prepend, SpacingMark, L, V, T, LV, LVT, Pictograph }

    private static Break Property(int scalar)
    {
        var low = 0; var high = PropertyStarts.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if ((PropertyStarts[middle] >> 5) <= scalar) low = middle + 1;
            else high = middle - 1;
        }
        return (Break)(PropertyStarts[high] & 31);
    }

    public static List<int> Boundaries(string source)
    {
        var result = new List<int>();
        var previous = Break.Other; var regional = 0; var pictograph = false; var joinedPictograph = false;
        for (var offset = 0; offset < source.Length;)
        {
            var valid = Rune.TryGetRuneAt(source, offset, out var rune);
            var current = Property(valid ? rune.Value : 0xfffd);
            var controls = previous is Break.CR or Break.LF or Break.Control || current is Break.CR or Break.LF or Break.Control;
            var hangul = previous == Break.L && current is Break.L or Break.V or Break.LV or Break.LVT ||
                previous is Break.LV or Break.V && current is Break.V or Break.T || previous is Break.LVT or Break.T && current == Break.T;
            var attached = previous == Break.CR && current == Break.LF || !controls &&
                (hangul || current is Break.Extend or Break.ZWJ or Break.SpacingMark || previous == Break.Prepend ||
                 previous == Break.ZWJ && joinedPictograph && current == Break.Pictograph ||
                 previous == Break.RegionalIndicator && current == Break.RegionalIndicator && regional % 2 == 1);
            if (offset == 0 || !attached) result.Add(offset);
            joinedPictograph = current == Break.ZWJ && pictograph;
            if (current != Break.Extend) pictograph = current == Break.Pictograph;
            regional = current == Break.RegionalIndicator ? regional + 1 : 0;
            previous = current;
            offset += valid ? rune.Utf16SequenceLength : 1;
        }
        return result;
    }

    public static IEnumerable<(string Text, int Index)> Elements(string source)
    {
        var starts = Boundaries(source);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 == starts.Count ? source.Length : starts[index + 1];
            yield return (source[start..end], start);
        }
    }
}
