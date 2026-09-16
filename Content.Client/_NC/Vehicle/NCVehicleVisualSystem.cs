// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle;
using Robust.Client.GameObjects;

namespace Content.Client._NC.Vehicle;

/// <summary>Projects replicated vehicle state onto cheap, data-authored RSI layers.</summary>
public sealed partial class NCVehicleVisualSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprites = default!;

    private readonly Dictionary<EntityUid, VisualMask> _cached = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCVehicleComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCVehicleComponent, AfterAutoHandleStateEvent>(OnVehicleState);
        SubscribeLocalEvent<NCVehicleBodyComponent, AfterAutoHandleStateEvent>(OnBodyState);
        SubscribeLocalEvent<NCVehicleAccessoriesComponent, AfterAutoHandleStateEvent>(OnAccessoriesState);
        SubscribeLocalEvent<NCVehicleConditionComponent, AfterAutoHandleStateEvent>(OnConditionState);
    }

    private void OnMapInit(Entity<NCVehicleComponent> ent, ref MapInitEvent args) => Refresh(ent);
    private void OnVehicleState(Entity<NCVehicleComponent> ent, ref AfterAutoHandleStateEvent args) => Refresh(ent);
    private void OnBodyState(Entity<NCVehicleBodyComponent> ent, ref AfterAutoHandleStateEvent args) => Refresh(ent.Owner);
    private void OnAccessoriesState(Entity<NCVehicleAccessoriesComponent> ent, ref AfterAutoHandleStateEvent args) => Refresh(ent.Owner);
    private void OnConditionState(Entity<NCVehicleConditionComponent> ent, ref AfterAutoHandleStateEvent args) => Refresh(ent.Owner);

    private void OnShutdown(Entity<NCVehicleComponent> ent, ref ComponentShutdown args)
    {
        _cached.Remove(ent);
    }

    /// <summary>Refreshes layers after another client system handles a state event it already owns.</summary>
    public void Refresh(EntityUid uid)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) || !TryComp<SpriteComponent>(uid, out var sprite))
            return;

        var mask = VisualMask.None;
        if (vehicle.HoodOpen)
            mask |= VisualMask.Hood;
        if (vehicle.StreetTuning)
            mask |= VisualMask.Tuning;

        if (TryComp<NCVehicleBodyComponent>(uid, out var body))
        {
            var deformation = 0f;
            foreach (var value in body.Deformation.Values)
                deformation = Math.Max(deformation, value);
            if (deformation >= vehicle.HeavyDamageVisualThreshold)
                mask |= VisualMask.DamageHeavy;
            else if (deformation >= vehicle.LightDamageVisualThreshold)
                mask |= VisualMask.DamageLight;
        }

        var powered = !TryComp<NCVehicleConditionComponent>(uid, out var condition) || condition.BatteryCharge > 0;
        if (TryComp<NCVehicleAccessoriesComponent>(uid, out var accessories) && accessories.Headlights && powered)
            mask |= VisualMask.Headlights;
        if (condition != null && (condition.EngineTemperature >= condition.OverheatTemperature || condition.EngineWear >= 0.65f))
            mask |= VisualMask.Smoke;

        if (TryComp<NCVehicleDriveComponent>(uid, out var drive))
        {
            if (powered && drive.Brake > 0.05f)
                mask |= VisualMask.Brakes;
            if (powered && drive.Gear < 0)
                mask |= VisualMask.Reverse;
            for (var wheel = 0; wheel < drive.WheelSpeeds.Length; wheel++)
            {
                if (NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Grip, wheel) < 0.4f ||
                    wheel < drive.WheelWear.Length && drive.WheelWear[wheel] >= 0.75f)
                {
                    mask |= VisualMask.Wheels;
                    break;
                }
            }
        }

        if (_cached.GetValueOrDefault(uid) == mask && _cached.ContainsKey(uid))
            return;
        _cached[uid] = mask;
        SetVisible(uid, sprite, NCVehicleVisualLayers.DamageLight, mask.HasFlag(VisualMask.DamageLight));
        SetVisible(uid, sprite, NCVehicleVisualLayers.DamageHeavy, mask.HasFlag(VisualMask.DamageHeavy));
        SetVisible(uid, sprite, NCVehicleVisualLayers.Headlights, mask.HasFlag(VisualMask.Headlights));
        SetVisible(uid, sprite, NCVehicleVisualLayers.Brakes, mask.HasFlag(VisualMask.Brakes));
        SetVisible(uid, sprite, NCVehicleVisualLayers.Reverse, mask.HasFlag(VisualMask.Reverse));
        SetVisible(uid, sprite, NCVehicleVisualLayers.Hood, mask.HasFlag(VisualMask.Hood));
        SetVisible(uid, sprite, NCVehicleVisualLayers.Wheels, mask.HasFlag(VisualMask.Wheels));
        SetVisible(uid, sprite, NCVehicleVisualLayers.Tuning, mask.HasFlag(VisualMask.Tuning));
        SetVisible(uid, sprite, NCVehicleVisualLayers.Smoke, mask.HasFlag(VisualMask.Smoke));
    }

    private void SetVisible(EntityUid uid, SpriteComponent sprite, NCVehicleVisualLayers key, bool visible)
    {
        if (_sprites.LayerMapTryGet((uid, sprite), key, out var layer, false))
            _sprites.LayerSetVisible((uid, sprite), layer, visible);
    }

    [Flags]
    private enum VisualMask : ushort
    {
        None = 0,
        DamageLight = 1 << 0,
        DamageHeavy = 1 << 1,
        Headlights = 1 << 2,
        Brakes = 1 << 3,
        Reverse = 1 << 4,
        Hood = 1 << 5,
        Wheels = 1 << 6,
        Tuning = 1 << 7,
        Smoke = 1 << 8,
    }
}
