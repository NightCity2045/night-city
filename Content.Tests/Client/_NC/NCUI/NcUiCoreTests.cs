// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Content.Client._NC.NCUI.Rendering;
using Content.Client._NC.NCUI.Spatial;
using Content.Client._NC.NCUI.State;
using Content.Client._NC.NCUI.Virtualization;
using Content.Shared._NC.NCUI;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Client._NC.NCUI;

[TestFixture]
public sealed class NcUiCoreTests
{
    [Test]
    public void NodeStorePreservesIdsAcrossSwapRemoval()
    {
        var store = new UiNodeStore();
        var visual = new UiVisualHandle(1);
        var first = store.Add(new UiSurfaceNode(new UIBox2(0, 0, 10, 10), visual));
        var middle = store.Add(new UiSurfaceNode(new UIBox2(10, 0, 20, 10), visual));
        var last = store.Add(new UiSurfaceNode(new UIBox2(20, 0, 30, 10), visual));

        Assert.That(store.Remove(middle, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(store.Contains(first), Is.True);
            Assert.That(store.Contains(middle), Is.False);
            Assert.That(store.Contains(last), Is.True);
            Assert.That(store.TryGet(last, out var node) && node.Id == last, Is.True);
        });
    }

    [Test]
    public void UniformGridDeduplicatesAreaResults()
    {
        var grid = new UniformGridSpatialIndex(10f);
        var id = new UiNodeId(1);
        grid.Insert(id, new UIBox2(0, 0, 25, 25));
        Span<UiNodeId> results = stackalloc UiNodeId[8];

        var count = grid.QueryArea(new UIBox2(0, 0, 30, 30), results, out var truncated);
        var result = results[0];

        Assert.Multiple(() =>
        {
            Assert.That(truncated, Is.False);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(result, Is.EqualTo(id));
        });
    }

    [Test]
    public void ViewportZoomKeepsAnchorStable()
    {
        var viewport = new UiViewportTransform();
        viewport.Configure(new Vector2(1000, 800), 1.5f);
        viewport.SetCenter(new Vector2(40, 70));
        var pixel = new Vector2(730, 215);
        var before = viewport.PixelToSurface(pixel);

        viewport.ZoomAt(pixel, 2f);

        Assert.That(Vector2.Distance(before, viewport.PixelToSurface(pixel)), Is.LessThan(0.0001f));
    }

    [Test]
    public void VirtualCollectionSelectionUsesStableKeys()
    {
        var view = new VirtualCollectionView<(int Id, string Name), int>(entry => entry.Id);
        view.Reset([(1, "c"), (2, "a"), (3, "b")]);
        view.SelectOnly(2);
        view.SetSort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        view.SetFilter(entry => entry.Id != 3);

        Assert.Multiple(() =>
        {
            Assert.That(view.Selection, Does.Contain(2));
            Assert.That(view.IndexOf(2), Is.EqualTo(0));
            Assert.That(view.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public void PointCompressionStaysWithinQuantum()
    {
        Vector2[] points = [new(10f, 15f), new(10.33f, 16.77f), new(40.1f, -3.25f)];
        var compressed = NcUiPointCompression.Compress(points, points[0], NcUiPointCompression.DefaultQuantum);
        var restored = NcUiPointCompression.Decompress(compressed, points[0], NcUiPointCompression.DefaultQuantum);

        for (var i = 0; i < points.Length; i++)
            Assert.That(Vector2.Distance(points[i], restored[i]), Is.LessThanOrEqualTo(0.09f));
    }

    [Test]
    public void RevisionOnlyAcceptsImmediateSuccessor()
    {
        var current = new NcUiRevision(41);
        Assert.Multiple(() =>
        {
            Assert.That(new NcUiRevision(42).IsImmediatelyAfter(current), Is.True);
            Assert.That(new NcUiRevision(43).IsImmediatelyAfter(current), Is.False);
        });
    }

    [Test]
    public void RevisionAndSequenceSkipReservedZeroOnWrap()
    {
        var revision = new NcUiRevision(uint.MaxValue).Next();
        var sequence = new NcUiActionSequence(uint.MaxValue).Next();

        Assert.Multiple(() =>
        {
            Assert.That(revision, Is.EqualTo(NcUiRevision.Initial));
            Assert.That(sequence, Is.EqualTo(NcUiActionSequence.Initial));
            Assert.That(revision.IsImmediatelyAfter(new NcUiRevision(uint.MaxValue)), Is.True);
            Assert.That(sequence.IsAfter(new NcUiActionSequence(uint.MaxValue)), Is.True);
        });
    }

    [Test]
    public void RenderQueueRebuildsOnlyAfterSpatialOrCameraChange()
    {
        var document = new UiSurfaceDocument(new UniformGridSpatialIndex(16f));
        var visual = document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Rectangle,
        });
        var node = document.AddNode(new UiSurfaceNode(new UIBox2(0, 0, 10, 10), visual,
            UiNodeFlags.Visible));
        var queue = new UiRenderQueue(document.Nodes);
        var viewport = new UIBox2(-20, -20, 20, 20);

        queue.Build(document, viewport);
        queue.Build(document, viewport);
        document.SetVisualState(node, UiNodeVisualState.Hovered);
        queue.Build(document, viewport);
        Assert.That(queue.RebuildCount, Is.EqualTo(1));

        document.SetBounds(node, new UIBox2(1, 1, 11, 11));
        queue.Build(document, viewport);
        Assert.That(queue.RebuildCount, Is.EqualTo(2));
    }

    [Test]
    public void RegistriesRecycleUnreferencedHandles()
    {
        var document = new UiSurfaceDocument();
        var firstVisual = document.Visuals.Register(new UiVisualDefinition { Kind = UiVisualKind.Rectangle });
        var firstShape = document.HitShapes.Register(new UiHitShapeDefinition { Kind = UiHitShapeKind.Ellipse });
        var node = new UiSurfaceNode(new UIBox2(0, 0, 10, 10), firstVisual)
        {
            HitShape = firstShape,
        };
        var id = document.AddNode(node);
        document.RemoveNode(id);

        var secondVisual = document.Visuals.Register(new UiVisualDefinition { Kind = UiVisualKind.Rectangle });
        var secondShape = document.HitShapes.Register(new UiHitShapeDefinition { Kind = UiHitShapeKind.Ellipse });
        Assert.Multiple(() =>
        {
            Assert.That(document.Visuals.Count, Is.EqualTo(1));
            Assert.That(document.HitShapes.Count, Is.EqualTo(1));
            Assert.That(secondVisual, Is.EqualTo(firstVisual));
            Assert.That(secondShape, Is.EqualTo(firstShape));
        });
    }

    [Test]
    public void VirtualTreeExpandsAndCollapsesByStableKey()
    {
        var leaf = new TestTreeNode(2, []);
        var root = new TestTreeNode(1, [leaf]);
        var tree = new VirtualTreeView<TestTreeNode, int>(node => node.Id, node => node.Children);
        tree.Reset([root]);
        Assert.That(tree.Count, Is.EqualTo(1));

        Assert.That(tree.Expand(1), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(tree.Count, Is.EqualTo(2));
            Assert.That(tree.DepthAt(1), Is.EqualTo(1));
            Assert.That(tree.KeyAt(1), Is.EqualTo(2));
        });

        Assert.That(tree.Collapse(1), Is.True);
        Assert.That(tree.Count, Is.EqualTo(1));
    }

    [Test]
    public void PatchGapRequestsResyncWithoutApplying()
    {
        var applier = new UiPatchApplier();
        var session = new NcUiSessionId(9);
        applier.ApplySnapshot(new TestSnapshot(session, new NcUiRevision(10)), _ => { });
        var requested = false;
        applier.ResyncRequired += revision => requested = revision == new NcUiRevision(10);

        var result = applier.ApplyPatch(
            new TestPatch(session, new NcUiRevision(10), new NcUiRevision(12)), _ => Assert.Fail());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(UiPatchApplyResult.RequiresResync));
            Assert.That(requested, Is.True);
            Assert.That(applier.Revision, Is.EqualTo(new NcUiRevision(10)));
        });
    }

    [Test]
    public void PredictionRemainsUntilAcknowledgedRevisionArrives()
    {
        var state = new TestPredictionState();
        var buffer = new UiPredictionBuffer<TestPredictionState>(state);
        var sequence = NcUiActionSequence.Initial;
        buffer.Predict(sequence, new AddPrediction(5));
        buffer.Resolve(new NcUiActionResultMessage(
            new NcUiSessionId(1), sequence, new NcUiRevision(3), NcUiActionResult.Accepted));
        Assert.That(state.Value, Is.EqualTo(5));

        buffer.ApplyAuthoritative(new NcUiRevision(3), value => value.Value = 5);
        Assert.Multiple(() =>
        {
            Assert.That(state.Value, Is.EqualTo(5));
            Assert.That(buffer.Count, Is.Zero);
        });
    }

    [Test]
    public void StrokeBatcherFiltersPointsAndFlushesAtCadence()
    {
        var batcher = new UiStrokeBatcher
        {
            MinimumPointDistance = 1f,
            FlushInterval = TimeSpan.FromMilliseconds(50),
        };
        Vector2[] batch = null;
        batcher.BatchReady += (points, _) => batch = points;
        batcher.Begin(Vector2.Zero, TimeSpan.Zero);
        batcher.Add(new Vector2(0.25f, 0f), TimeSpan.FromMilliseconds(10));
        batcher.Add(new Vector2(2f, 0f), TimeSpan.FromMilliseconds(60));

        Assert.That(batch, Has.Length.EqualTo(2));
    }

    private sealed record TestTreeNode(int Id, IReadOnlyList<TestTreeNode> Children);

    private sealed class TestSnapshot : NcUiSnapshotMessage
    {
        public TestSnapshot(NcUiSessionId session, NcUiRevision revision)
            : base(session, revision, NcUiPermission.View, [], [])
        {
        }
    }

    private sealed class TestPatch : NcUiPatchMessage
    {
        public TestPatch(NcUiSessionId session, NcUiRevision previous, NcUiRevision revision)
            : base(session, previous, revision)
        {
        }
    }

    private sealed class TestPredictionState
    {
        public int Value;
    }

    private sealed class AddPrediction(int amount) : IUiPrediction<TestPredictionState>
    {
        public void Apply(TestPredictionState state) => state.Value += amount;
        public void Rollback(TestPredictionState state) => state.Value -= amount;
    }
}
