// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.Shared._NC.Vehicle.Prototypes;

namespace Content.Shared._NC.Vehicle.Workshop;

/// <summary>Canonical picking and occlusion, usable without GPU resources on the server.</summary>
public static class NCVehicleWorkshopGeometry
{
    public static (Vector2 Position, Vector2 Size) Layout(NCVehicleWorkshopPrototype profile, string id,
        NCVehicleWorkshopNode node)
    {
        var position = node.Position;
        var size = node.Size;
        if (profile.ViewLayouts.TryGetValue(node.View, out var view))
        {
            position = view.Offset + position * view.Scale;
            size *= view.Scale;
        }
        if (profile.NodeLayouts.TryGetValue(id, out var correction))
        {
            position += correction.Offset;
            size *= correction.Scale;
        }
        return (position, size);
    }

    public static Vector2 AccessPosition(NCVehicleWorkshopPrototype profile, string id, NCVehicleWorkshopView view)
        => profile.ViewLayouts.TryGetValue(id, out var layout) && layout.AccessPosition is { } position
            ? position
            : view.AccessPosition;

    public static float AccessRange(NCVehicleWorkshopPrototype profile, string id, NCVehicleWorkshopView view)
        => profile.ViewLayouts.TryGetValue(id, out var layout) && layout.AccessRange is { } range
            ? range
            : view.AccessRange;

    public static Vector2 LocalPoint(NCVehicleWorkshopPrototype profile, string id, NCVehicleWorkshopNode node,
        Vector2 position)
    {
        var layout = Layout(profile, id, node);
        return (position - layout.Position) / layout.Size;
    }

    public static bool Contains(NCVehicleWorkshopPrototype profile, string id, NCVehicleWorkshopNode node,
        Vector2 position)
    {
        var layout = Layout(profile, id, node);
        return Contains(node, layout.Position, layout.Size, position);
    }

    public static bool Contains(NCVehicleWorkshopNode node, Vector2 position)
        => Contains(node, node.Position, node.Size, position);

    private static bool Contains(NCVehicleWorkshopNode node, Vector2 nodePosition, Vector2 nodeSize, Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || nodeSize.X <= 0 || nodeSize.Y <= 0)
            return false;
        var local = (position - nodePosition) / nodeSize;
        if (local.X is < 0 or > 1 || local.Y is < 0 or > 1 ||
            node.Ellipse && (local - new Vector2(0.5f)).LengthSquared() > 0.25f)
            return false;
        if (node.HitMask.Length == 0) return true;
        var row = node.HitMask[Math.Min((int) (local.Y * node.HitMask.Length), node.HitMask.Length - 1)];
        return row.Length > 0 && row[Math.Min((int) (local.X * row.Length), row.Length - 1)] == '1';
    }

    public static bool Exposed(NCVehicleSlotDefinition slot, Func<string, bool> installed)
    {
        foreach (var blocker in slot.BlockedBy)
            if (installed(blocker))
                return false;
        return true;
    }

    public static bool Valid(NCVehicleWorkshopPrototype profile, NCVehicleChassisPrototype chassis)
    {
        if (profile.Views.Count == 0 || profile.Nodes.Count == 0 ||
            !NCVehicleDriveMath.Positive(profile.PointSeconds) || !NCVehicleDriveMath.Positive(profile.TransferSeconds) ||
            !NCVehicleDriveMath.Positive(profile.InspectSeconds) || !NCVehicleDriveMath.Positive(profile.RefreshSeconds))
            return false;
        foreach (var (id, view) in profile.Views)
        {
            var accessPosition = AccessPosition(profile, id, view);
            if (!NCVehicleDriveMath.Positive(AccessRange(profile, id, view)) ||
                !float.IsFinite(accessPosition.X) || !float.IsFinite(accessPosition.Y))
                return false;
        }
        foreach (var (id, node) in profile.Nodes)
        {
            var layout = Layout(profile, id, node);
            if (!profile.Views.ContainsKey(node.View) || !chassis.Slots.ContainsKey(node.Slot) ||
                !NCVehicleDriveMath.Positive(layout.Size.X) || !NCVehicleDriveMath.Positive(layout.Size.Y) ||
                !float.IsFinite(layout.Position.X) || !float.IsFinite(layout.Position.Y) ||
                layout.Position.X < 0 || layout.Position.Y < 0 || layout.Position.X + layout.Size.X > 1.001f ||
                layout.Position.Y + layout.Size.Y > 1.001f)
                return false;
        }
        foreach (var layout in profile.ViewLayouts.Values)
            if (!NCVehicleDriveMath.Positive(layout.Scale.X) || !NCVehicleDriveMath.Positive(layout.Scale.Y) ||
                !float.IsFinite(layout.Offset.X) || !float.IsFinite(layout.Offset.Y) ||
                layout.AccessRange is { } range && !NCVehicleDriveMath.Positive(range))
                return false;
        foreach (var layout in profile.NodeLayouts.Values)
            if (!NCVehicleDriveMath.Positive(layout.Scale.X) || !NCVehicleDriveMath.Positive(layout.Scale.Y) ||
                !float.IsFinite(layout.Offset.X) || !float.IsFinite(layout.Offset.Y))
                return false;
        foreach (var (id, slot) in chassis.Slots)
        {
            foreach (var blocker in slot.BlockedBy)
                if (blocker == id || !chassis.Slots.ContainsKey(blocker))
                    return false;
            foreach (var child in slot.ExtractWith)
                if (child == id || !chassis.Slots.ContainsKey(child)) return false;
            foreach (var point in slot.WorkPoints.Values)
                if (!NCVehicleDriveMath.Positive(point.Radius) || point.Position.X is < 0 or > 1 || point.Position.Y is < 0 or > 1 ||
                    !float.IsFinite(point.Position.X) || !float.IsFinite(point.Position.Y))
                    return false;
        }
        foreach (var node in profile.Nodes.Values)
            if (node.HitMask.Length > 64 || node.HitMask.Any(row => row.Length != node.HitMask.Length || row.Any(c => c is not ('0' or '1'))))
                return false;
        // An access or carrier cycle makes a part permanently unreachable. Support cycles are checked by the assembly compiler.
        foreach (var id in chassis.Slots.Keys)
            if (Cycle(id, new(), new(), chassis, false) || Cycle(id, new(), new(), chassis, true)) return false;
        return true;
    }

    private static bool Cycle(string id, HashSet<string> visiting, HashSet<string> visited,
        NCVehicleChassisPrototype chassis, bool carriers)
    {
        if (visiting.Contains(id)) return true;
        if (!visited.Add(id)) return false;
        visiting.Add(id);
        foreach (var next in carriers ? chassis.Slots[id].ExtractWith : chassis.Slots[id].BlockedBy)
            if (Cycle(next, visiting, visited, chassis, carriers)) return true;
        visiting.Remove(id);
        return false;
    }
}
