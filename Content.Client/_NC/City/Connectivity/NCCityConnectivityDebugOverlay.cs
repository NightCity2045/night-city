// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Maths;

namespace Content.Client._NC.City.Connectivity;

/// <summary>
/// Draws only the current logical floor from the server-provided diagnostic snapshot.
/// </summary>
public sealed class NCCityConnectivityDebugOverlay : Overlay
{
    private readonly NCCityConnectivityDebugClientSystem _system;
    private readonly Font _font;

    public override OverlaySpace Space => OverlaySpace.WorldSpace | OverlaySpace.ScreenSpace;

    public NCCityConnectivityDebugOverlay(NCCityConnectivityDebugClientSystem system)
    {
        _system = system;
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(
            cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"),
            9);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_system.Snapshot is not { Enabled: true } snapshot ||
            !_system.TryGetViewCoordinates(out var view))
        {
            return;
        }

        if (args.Space == OverlaySpace.ScreenSpace)
        {
            DrawLabels(args, snapshot, view);
            return;
        }

        DrawWorld(args, snapshot, view);
    }

    private void DrawWorld(
        in OverlayDrawArgs args,
        NCCityConnectivityDebugSnapshot snapshot,
        NCMapCoordinates view)
    {
        var bounds = args.WorldBounds.CalcBoundingBox().Enlarged(1f);
        foreach (var portal in snapshot.RoomPortals)
        {
            if (!OnFloor(portal.TargetA, view) || !OnFloor(portal.TargetB, view) ||
                !SegmentCanAffect(bounds, portal.TargetA.Position, portal.TargetB.Position))
            {
                continue;
            }

            var color = portal.Kind == NCConnectivityDebugRoomPortalKind.Door
                ? Color.Cyan
                : Color.Lime;
            args.WorldHandle.DrawLine(portal.TargetA.Position, portal.TargetB.Position, color);
            DrawMarker(args.WorldHandle, portal.TargetA.Position, color, 0.08f);
            DrawMarker(args.WorldHandle, portal.TargetB.Position, color, 0.08f);
        }

        foreach (var portal in snapshot.CityPortals)
        {
            var color = !portal.Enabled
                ? Color.Red
                : portal.Kind == NCCityPortalKind.Vertical
                    ? Color.Magenta
                    : Color.Yellow;
            if (OnFloor(portal.EntryTarget, view))
                DrawMarker(args.WorldHandle, portal.EntryTarget.Position, color, 0.14f);
            if (OnFloor(portal.ExitTarget, view))
                DrawMarker(args.WorldHandle, portal.ExitTarget.Position, color, 0.14f);
            if (OnFloor(portal.EntryTarget, view) && OnFloor(portal.ExitTarget, view) &&
                SegmentCanAffect(bounds, portal.EntryTarget.Position, portal.ExitTarget.Position))
            {
                args.WorldHandle.DrawLine(portal.EntryTarget.Position, portal.ExitTarget.Position, color);
            }
        }

        foreach (var room in snapshot.Rooms)
        {
            if (!OnFloor(room.Anchor, view) || !bounds.Contains(room.Anchor.Position))
                continue;

            var color = GetZoneColor(room.ZoneId);
            DrawMarker(args.WorldHandle, room.Anchor.Position, color, 0.11f);
        }
    }

    private void DrawLabels(
        in OverlayDrawArgs args,
        NCCityConnectivityDebugSnapshot snapshot,
        NCMapCoordinates view)
    {
        var issueCount = snapshot.Issues.Length;
        args.ScreenHandle.DrawString(
            _font,
            new Vector2(12f, 150f),
            $"CityGraph v{snapshot.CityGraphVersion} | RoomGraph v{snapshot.RoomGraphVersion} | " +
            $"Spawn v{snapshot.SpawnIndexVersion} | issues {issueCount}",
            issueCount == 0 ? Color.Lime : Color.Orange);

        foreach (var portal in snapshot.CityPortals)
        {
            if (!OnFloor(portal.EntryTarget, view) || args.ViewportControl == null)
                continue;

            var screen = args.ViewportControl.WorldToScreen(portal.EntryTarget.Position);
            var cost = portal.TravelSeconds + portal.ThreatCost;
            args.ScreenHandle.DrawString(
                _font,
                screen + new Vector2(4f, -4f),
                $"C{portal.Id} {portal.Kind} {cost:0.#}",
                portal.Enabled ? Color.Yellow : Color.Red);
        }

        foreach (var room in snapshot.Rooms)
        {
            if (!OnFloor(room.Anchor, view) || args.ViewportControl == null)
                continue;

            var screen = args.ViewportControl.WorldToScreen(room.Anchor.Position);
            args.ScreenHandle.DrawString(
                _font,
                screen + new Vector2(4f, 4f),
                $"R{room.Id}/{ShortZone(room.ZoneId)}",
                GetZoneColor(room.ZoneId));
        }
    }

    private Color GetZoneColor(NCZoneId zoneId)
    {
        if (_system.ErrorZones.Contains(zoneId))
            return Color.Red;
        if (_system.WarningZones.Contains(zoneId))
            return Color.Orange;
        return Color.White;
    }

    private static bool OnFloor(NCMapCoordinates target, NCMapCoordinates view)
    {
        return target.NetworkId == view.NetworkId && target.Z == view.Z;
    }

    private static bool SegmentCanAffect(Box2 bounds, Vector2 start, Vector2 end)
    {
        return new Box2(Vector2.Min(start, end), Vector2.Max(start, end)).Intersects(bounds);
    }

    private static void DrawMarker(DrawingHandleWorld handle, Vector2 position, Color color, float radius)
    {
        handle.DrawRect(Box2.FromDimensions(
            position - new Vector2(radius),
            new Vector2(radius * 2f)), color);
    }

    private static string ShortZone(NCZoneId zoneId)
    {
        var value = zoneId.Value.ToString("N");
        return value[..6];
    }
}
