// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;

namespace Content.Server._NC.Medical.Wounds;

/// <summary>
/// Projects private wound records into a bounded, presentation-only component replicated through PVS.
/// The projection is rebuilt only after medical mutations; it never scans all bodies in Update.
/// </summary>
public sealed partial class NCVisibleWoundSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCVisibleWoundComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<NCVisibleWoundComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCVisibleWoundComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
        SubscribeLocalEvent<NCVisibleWoundComponent, NCWoundCreatedEvent>(OnWoundMutation);
        SubscribeLocalEvent<NCVisibleWoundComponent, NCWoundChangedEvent>(OnWoundMutation);
        SubscribeLocalEvent<NCVisibleWoundComponent, NCWoundProgressedEvent>(OnWoundMutation);
    }

    private void OnComponentInit(Entity<NCVisibleWoundComponent> entity, ref ComponentInit args)
    {
        // The marker list is a derived network projection, never prototype-authored state. Give every entity
        // its own fresh collection so pooled components and inherited prototype data cannot leak wounds across
        // respawns, test-pair recycling or round boundaries.
        entity.Comp.Markers = [];
    }

    private void OnMapInit(Entity<NCVisibleWoundComponent> entity, ref MapInitEvent args)
    {
        Rebuild(entity);
    }

    private void OnMedicalBodyChanged(
        Entity<NCVisibleWoundComponent> entity,
        ref NCMedicalBodyChangedEvent args)
    {
        if ((args.Domains & (NCMedicalDirtyDomains.Anatomy |
                             NCMedicalDirtyDomains.Wounds |
                             NCMedicalDirtyDomains.Diagnostics)) == 0)
        {
            return;
        }

        Rebuild(entity);
    }

    private void OnWoundMutation(Entity<NCVisibleWoundComponent> entity, ref NCWoundCreatedEvent args)
    {
        Rebuild(entity);
    }

    private void OnWoundMutation(Entity<NCVisibleWoundComponent> entity, ref NCWoundChangedEvent args)
    {
        Rebuild(entity);
    }

    private void OnWoundMutation(Entity<NCVisibleWoundComponent> entity, ref NCWoundProgressedEvent args)
    {
        Rebuild(entity);
    }

    /// <summary>
    /// Rebuilds one body's public visual projection. Active wounds and bounded permanent scars already have
    /// prototype-controlled caps, so this operation cannot grow with city population or playtime.
    /// </summary>
    public void Rebuild(Entity<NCVisibleWoundComponent> entity)
    {
        var markers = new List<NCVisibleWoundMarker>();
        if (TryComp<NCWoundContainerComponent>(entity.Owner, out var wounds))
        {
            markers.EnsureCapacity(wounds.Wounds.Count + wounds.Scars.Count);
            foreach (var wound in wounds.Wounds.Values)
            {
                markers.Add(new NCVisibleWoundMarker(
                    wound.TargetZone,
                    wound.WoundType,
                    wound.Severity,
                    wound.Stage,
                    wound.VisualX,
                    wound.VisualY,
                    wound.VisualDirection,
                    wound.Surface));
            }

            foreach (var scar in wounds.Scars)
            {
                markers.Add(new NCVisibleWoundMarker(
                    scar.TargetZone,
                    scar.WoundType,
                    scar.OriginalSeverity,
                    NCWoundStage.Scarred,
                    scar.VisualX,
                    scar.VisualY,
                    scar.VisualDirection,
                    scar.Surface));
            }

            markers.Sort(CompareMarkers);
        }

        if (MarkersEqual(entity.Comp.Markers, markers))
            return;

        entity.Comp.Markers = markers;
        Dirty(entity, entity.Comp);
    }

    private static int CompareMarkers(NCVisibleWoundMarker left, NCVisibleWoundMarker right)
    {
        var result = string.CompareOrdinal(left.Zone.Id, right.Zone.Id);
        if (result != 0)
            return result;
        result = left.Surface.CompareTo(right.Surface);
        if (result != 0)
            return result;
        result = left.VisualY.CompareTo(right.VisualY);
        if (result != 0)
            return result;
        result = left.VisualX.CompareTo(right.VisualX);
        if (result != 0)
            return result;
        result = left.Type.CompareTo(right.Type);
        if (result != 0)
            return result;
        result = left.Stage.CompareTo(right.Stage);
        if (result != 0)
            return result;
        return left.Severity.CompareTo(right.Severity);
    }

    private static bool MarkersEqual(
        IReadOnlyList<NCVisibleWoundMarker> left,
        IReadOnlyList<NCVisibleWoundMarker> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }

        return true;
    }
}
