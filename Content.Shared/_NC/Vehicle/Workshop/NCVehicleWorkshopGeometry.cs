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
    public static bool Contains(NCVehicleWorkshopNode node, Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || node.Size.X <= 0 || node.Size.Y <= 0)
            return false;
        var local = (position - node.Position) / node.Size;
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
        foreach (var view in profile.Views.Values)
            if (!NCVehicleDriveMath.Positive(view.AccessRange) || !float.IsFinite(view.AccessPosition.X) || !float.IsFinite(view.AccessPosition.Y))
                return false;
        foreach (var node in profile.Nodes.Values)
            if (!profile.Views.ContainsKey(node.View) || !chassis.Slots.ContainsKey(node.Slot) ||
                !NCVehicleDriveMath.Positive(node.Size.X) || !NCVehicleDriveMath.Positive(node.Size.Y) ||
                !float.IsFinite(node.Position.X) || !float.IsFinite(node.Position.Y) ||
                node.Position.X < 0 || node.Position.Y < 0 || node.Position.X + node.Size.X > 1.001f || node.Position.Y + node.Size.Y > 1.001f)
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
