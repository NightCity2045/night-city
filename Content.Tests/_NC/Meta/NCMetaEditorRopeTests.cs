// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using Content.Client._NC.Meta.Editor;
using NUnit.Framework;
using Robust.Shared.Utility;

namespace Content.Tests._NC.Meta;

[TestFixture]
public sealed class NCMetaEditorRopeTests
{
    [Test]
    public void RepeatedBoundaryEditsStayBalancedAndPreserveHistory()
    {
        var text = "начало🙂e\u0301";
        var rope = NCMetaEditorRope.Create(text);
        var snapshots = new List<(Rope.Node Node, string Text)>();
        for (var edit = 0; edit < 5000; edit++)
        {
            if (edit % 100 == 0) snapshots.Add((rope, text));
            var at = edit % 2 == 0 ? 0 : text.Length;
            rope = NCMetaEditorRope.Replace(rope, at, 0, "x");
            text = text.Insert(at, "x");
            if (edit % 3 == 0)
            {
                rope = NCMetaEditorRope.Replace(rope, at, 1, "");
                text = text.Remove(at, 1);
            }
            Assert.That(rope.Depth, Is.LessThan(16));
        }
        Assert.That(Rope.Collapse(rope), Is.EqualTo(text));
        foreach (var snapshot in snapshots) Assert.That(Rope.Collapse(snapshot.Node), Is.EqualTo(snapshot.Text));
    }

    [Test]
    public void DeleteWholeDocumentThenTypeUnicodeRepeatedly()
    {
        var rope = NCMetaEditorRope.Create("");
        for (var edit = 0; edit < 1000; edit++)
        {
            rope = NCMetaEditorRope.Replace(rope, 0, 0, "Ж🙂e\u0301");
            Assert.That(Rope.Collapse(rope), Is.EqualTo("Ж🙂e\u0301"));
            rope = NCMetaEditorRope.Replace(rope, 0, 5, "");
            Assert.That(Rope.Collapse(rope), Is.Empty);
        }
    }

    [Test]
    public void LargeUnicodeSnapshotDoesNotSplitSurrogatePairsBetweenLeaves()
    {
        var text = string.Concat(Enumerable.Repeat("Ж🙂e\u0301", 25000));
        var rope = NCMetaEditorRope.Create(text);
        Assert.That(Rope.Collapse(rope), Is.EqualTo(text));
        Assert.That(rope.Depth, Is.LessThan(16));
        foreach (var leaf in Rope.CollectLeaves(rope))
        {
            Assert.That(leaf.Text, Is.Not.Empty);
            Assert.That(char.IsLowSurrogate(leaf.Text[0]), Is.False);
            Assert.That(char.IsHighSurrogate(leaf.Text[^1]), Is.False);
        }
        var replaced = NCMetaEditorRope.Replace(rope, 0, text.Length, "новый🙂");
        Assert.That(Rope.Collapse(replaced), Is.EqualTo("новый🙂"));
        Assert.That(Rope.Collapse(rope), Is.EqualTo(text));
        Assert.Throws<ArgumentOutOfRangeException>(() => NCMetaEditorRope.Replace(rope, 0, 0, new string('x', NCMetaCodeEditor.MaximumBytes)));
    }
}
