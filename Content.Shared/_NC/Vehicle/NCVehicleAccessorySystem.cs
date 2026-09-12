// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Light;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._NC.Vehicle;

/// <summary>Shared electrical switches; authoritative chemistry consumes washer fluid through a local event.</summary>
public sealed partial class NCVehicleAccessorySystem : EntitySystem
{
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedPointLightSystem _lights = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleAccessoriesComponent, NCVehicleMaintenanceEvent>(OnMaintenance);
    }

    public bool TryUse(EntityUid uid, EntityUid user, NCVehicleDriverAction action)
    {
        if (!TryComp<NCVehicleAccessoriesComponent>(uid, out var state) ||
            !TryComp<NCVehicleDriveComponent>(uid, out var drive) || drive.Driver != user ||
            !_blocker.CanConsciouslyPerformAction(user) ||
            !TryComp<NCVehicleConditionComponent>(uid, out var condition))
            return false;
        // A powerless lamp must still be switchable off before a replacement battery is connected.
        if (condition.BatteryCharge <= 0 && !(action == NCVehicleDriverAction.Lights && state.Headlights))
            return false;
        switch (action)
        {
            case NCVehicleDriverAction.Lights:
                state.Headlights = !state.Headlights;
                RefreshLights(uid, state, drive, condition);
                break;
            case NCVehicleDriverAction.Horn:
                if (_timing.CurTime < state.NextHorn ||
                    NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Horn) <= 0)
                    return false;
                state.NextHorn = _timing.CurTime + TimeSpan.FromSeconds(state.HornCooldown);
                _audio.PlayPredicted(state.HornSound, uid, user);
                break;
            case NCVehicleDriverAction.Wash:
                if (state.WasherCapacity <= 0 || state.WasherVolume <= 0 ||
                    condition.WasherFraction * state.WasherCapacity < state.WasherVolume ||
                    NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Washer) <= 0)
                    return false;
                condition.WasherFraction = Math.Max(0, condition.WasherFraction - state.WasherVolume / state.WasherCapacity);
                state.WindshieldDirt = Math.Max(0, state.WindshieldDirt - state.WashAmount);
                var wash = new NCVehicleWasherUsedEvent(state.WasherVolume);
                RaiseLocalEvent(uid, ref wash);
                Dirty(uid, condition);
                break;
            default:
                return false;
        }
        Dirty(uid, state);
        return true;
    }

    public void PlayStartup(EntityUid uid, EntityUid user)
    {
        if (TryComp<NCVehicleAccessoriesComponent>(uid, out var state))
            _audio.PlayPredicted(state.StartupSound, uid, user);
    }

    private void OnMaintenance(Entity<NCVehicleAccessoriesComponent> ent, ref NCVehicleMaintenanceEvent args)
    {
        if (!TryComp<NCVehicleDriveComponent>(ent, out var drive) || !TryComp<NCVehicleConditionComponent>(ent, out var condition))
            return;
        ent.Comp.WindshieldDirt = Math.Clamp(ent.Comp.WindshieldDirt +
            drive.PreviousVelocity.Length() * ent.Comp.DirtPerMeter * args.Seconds, 0, 1);
        if (ent.Comp.Headlights)
            condition.BatteryCharge = Math.Max(0, condition.BatteryCharge - ent.Comp.HeadlightAmps * args.Seconds / 3600);
        RefreshLights(ent, ent.Comp, drive, condition);
        Dirty(ent);
        Dirty(ent.Owner, condition);
    }

    private void RefreshLights(EntityUid uid, NCVehicleAccessoriesComponent state, NCVehicleDriveComponent drive, NCVehicleConditionComponent condition)
    {
        if (_lights.TryGetLight(uid, out var light))
            _lights.SetEnabled(uid, state.Headlights && condition.BatteryCharge > 0 &&
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Lights) > 0, comp: light);
    }
}

[ByRefEvent]
public readonly record struct NCVehicleWasherUsedEvent(float Volume);
