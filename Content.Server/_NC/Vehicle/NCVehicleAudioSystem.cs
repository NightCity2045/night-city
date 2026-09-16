// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;

namespace Content.Server._NC.Vehicle;

/// <summary>One PVS-scoped engine loop per running chassis, released on shutdown or component removal.</summary>
public sealed partial class NCVehicleAudioSystem : EntitySystem
{
    [Dependency] private SharedAudioSystem _audio = default!;
    private readonly Dictionary<EntityUid, EntityUid> _loops = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleMaintenanceEvent>(OnMaintenance);
        SubscribeLocalEvent<NCVehicleAccessoriesComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCVehicleAccessoriesComponent, NCVehicleDriveShutdownEvent>(OnDriveShutdown);
    }

    private void OnMaintenance(Entity<NCVehicleDriveComponent> ent, ref NCVehicleMaintenanceEvent args)
    {
        if (!ent.Comp.EngineRunning || !TryComp<NCVehicleAccessoriesComponent>(ent, out var accessories))
        {
            Stop(ent);
            return;
        }
        if (!_loops.TryGetValue(ent, out var loop) || TerminatingOrDeleted(loop))
        {
            var played = _audio.PlayPvs(accessories.IdleSound, ent.Owner, AudioParams.Default.WithLoop(true));
            if (played == null)
                return;
            loop = played.Value.Entity;
            _loops[ent] = loop;
        }
        _audio.SetVolume(loop, accessories.IdleVolume + (accessories.LoadVolume - accessories.IdleVolume) * ent.Comp.Throttle);
    }

    private void OnShutdown(Entity<NCVehicleAccessoriesComponent> ent, ref ComponentShutdown args) => Stop(ent);
    private void OnDriveShutdown(Entity<NCVehicleAccessoriesComponent> ent, ref NCVehicleDriveShutdownEvent args) => Stop(ent);
    private void Stop(EntityUid uid)
    {
        if (_loops.Remove(uid, out var loop))
            _audio.Stop(loop);
    }
}
