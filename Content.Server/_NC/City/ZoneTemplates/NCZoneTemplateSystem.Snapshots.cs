// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.ZoneTemplates;

public sealed partial class NCZoneTemplateSystem
{
    private readonly Dictionary<ProtoId<NCZoneTemplatePrototype>, NCZoneTemplateEntitySnapshot>
        _preparedEntitySnapshots = [];

    /// <summary>
    /// Replaces all persisted apartment entity graphs and rebuilds only unused prepared template instances.
    /// Active deployments own their already-leased instances and are never mutated here.
    /// </summary>
    public void ReplacePreparedEntitySnapshots(
        IReadOnlyDictionary<ProtoId<NCZoneTemplatePrototype>, NCZoneTemplateEntitySnapshot> snapshots)
    {
        _preparedEntitySnapshots.Clear();
        foreach (var (templateId, snapshot) in snapshots)
            _preparedEntitySnapshots[templateId] = snapshot;

        RebuildPreparedTemplateCache();
    }

    public void RemovePreparedEntitySnapshot(ProtoId<NCZoneTemplatePrototype> templateId)
    {
        if (!_preparedEntitySnapshots.Remove(templateId))
            return;

        RebuildPreparedTemplateCache();
    }

    private void RebuildPreparedTemplateCache()
    {
        ClearPreparedTemplateCache();
        foreach (var template in _prototypes.EnumeratePrototypes<NCZoneTemplatePrototype>())
        {
            if (template.CachePolicy is NCZoneTemplateCachePolicy.Always or NCZoneTemplateCachePolicy.Warm)
                QueueTemplateCache(template);
        }
    }

    private bool TryApplyPreparedEntitySnapshot(
        ProtoId<NCZoneTemplatePrototype> templateId,
        int templateRevision,
        int relativeZ,
        EntityUid sourceGrid,
        Vector2i sourceOrigin,
        out string error)
    {
        error = string.Empty;
        if (!_preparedEntitySnapshots.TryGetValue(templateId, out var snapshot))
            return true;
        if (snapshot.TemplateRevision != templateRevision)
        {
            error = $"Persisted snapshot revision {snapshot.TemplateRevision} does not match template revision {templateRevision}.";
            return false;
        }
        if (!snapshot.Floors.TryGetValue(relativeZ, out var floor))
        {
            error = $"Persisted snapshot has no entity graph for relative Z={relativeZ}.";
            return false;
        }

        // Tiles always come from the versioned mapper template. Its clean entity roots are replaced by the last
        // complete snapshot so moved/destroyed furniture and resident belongings are not duplicated.
        var cleanRoots = new List<EntityUid>();
        var children = Transform(sourceGrid).ChildEnumerator;
        while (children.MoveNext(out var child))
            cleanRoots.Add(child);
        foreach (var child in cleanRoots)
            Del(child);

        if (string.IsNullOrWhiteSpace(floor.EntityYaml))
            return true;

        using var reader = new StringReader(floor.EntityYaml);
        // A floor with one root is categorized as Entity, while multiple roots are Unknown. Both are valid here;
        // structural validation below still rejects maps, grids and non-orphan roots.
        var options = MapLoadOptions.Default;
        if (!_loader.TryLoadGeneric(reader, $"property snapshot {templateId} Z={relativeZ}", out var result, options))
        {
            error = $"Persisted entity graph for relative Z={relativeZ} could not be loaded.";
            return false;
        }

        if (result.Maps.Count != 0 || result.Grids.Count != 0 ||
            result.RootNodes.Count != result.Orphans.Count)
        {
            DeleteSnapshotLoad(result.RootNodes);
            error = "Persisted apartment snapshot contains a map, grid or non-orphan root.";
            return false;
        }

        foreach (var uid in result.Entities)
            RemComp<NCZoneDeploymentComponent>(uid);

        var savedOrigin = new Vector2(floor.SavedLocalOriginX, floor.SavedLocalOriginY);
        var preparedOrigin = new Vector2(sourceOrigin.X, sourceOrigin.Y);
        foreach (var root in result.Orphans)
        {
            var transform = Transform(root);
            var position = preparedOrigin + (transform.LocalPosition - savedOrigin);
            var rotation = transform.LocalRotation;
            _transform.SetCoordinates(root, new EntityCoordinates(sourceGrid, position));
            _transform.SetLocalRotation(root, rotation);
        }

        return true;
    }

    private void DeleteSnapshotLoad(IEnumerable<EntityUid> roots)
    {
        foreach (var root in roots)
        {
            if (Exists(root))
                Del(root);
        }
    }
}

public sealed record NCZoneTemplateEntitySnapshot(
    int TemplateRevision,
    IReadOnlyDictionary<int, NCZoneTemplateEntitySnapshotFloor> Floors);

public sealed record NCZoneTemplateEntitySnapshotFloor(
    int RelativeZ,
    float SavedLocalOriginX,
    float SavedLocalOriginY,
    string EntityYaml);
