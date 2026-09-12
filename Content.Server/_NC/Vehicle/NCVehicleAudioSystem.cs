// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Vehicle;

/// <summary>Crossfades RPM bands and emits PVS-scoped driveline events for active player vehicles.</summary>
public sealed partial class NCVehicleAudioSystem : EntitySystem
{
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, VehicleAudioState> _states = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleMaintenanceEvent>(OnMaintenance);
        SubscribeLocalEvent<NCVehicleAccessoriesComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCVehicleAccessoriesComponent, NCVehicleDriveShutdownEvent>(OnDriveShutdown);
    }

    private void OnMaintenance(Entity<NCVehicleDriveComponent> ent, ref NCVehicleMaintenanceEvent args)
    {
        if (!ent.Comp.EngineRunning || !TryComp<NCVehicleAccessoriesComponent>(ent, out var accessories) ||
            !_prototypes.TryIndex(accessories.SoundProfile, out var profile) ||
            !_prototypes.TryIndex(ent.Comp.Handling, out var handling))
        {
            Stop(ent);
            return;
        }

        if (!_states.TryGetValue(ent, out var state))
        {
            state = new VehicleAudioState();
            _states.Add(ent, state);
        }

        EnsureLoop(ent, ref state.Low, profile.LowLoop, profile.MinimumLoopVolume);
        EnsureLoop(ent, ref state.Mid, profile.MidLoop, profile.MinimumLoopVolume);
        EnsureLoop(ent, ref state.High, profile.HighLoop, profile.MinimumLoopVolume);
        var rpm = Math.Clamp((ent.Comp.EngineRpm - handling.IdleRpm) /
            Math.Max(1, handling.RedlineRpm - handling.IdleRpm), 0, 1);
        var boost = ent.Comp.Throttle * profile.LoadVolumeBoost;
        SetLoopVolume(state.Low, BandVolume(profile, 1 - SmoothStep(0.18f, 0.58f, rpm)) + boost);
        SetLoopVolume(state.Mid, BandVolume(profile, Math.Clamp(1 - Math.Abs(rpm - 0.5f) / 0.38f, 0, 1)) + boost);
        SetLoopVolume(state.High, BandVolume(profile, SmoothStep(0.48f, 0.88f, rpm)) + boost);

        var turboWeight = Math.Clamp((rpm - profile.ForcedInductionStart) /
            Math.Max(0.01f, 1 - profile.ForcedInductionStart), 0, 1) * ent.Comp.Throttle;
        UpdateOptionalLoop(ent, ref state.ForcedInduction, profile.ForcedInductionLoop, turboWeight, profile);
        var slip = Math.Max(Math.Abs(ent.Comp.LongitudinalSlip), Math.Abs(ent.Comp.LateralSlip));
        var slipWeight = Math.Clamp((slip - profile.SlipStart) / Math.Max(0.01f, 1 - profile.SlipStart), 0, 1);
        UpdateOptionalLoop(ent, ref state.TyreSlip, profile.TyreSlipLoop, slipWeight, profile);

        if (state.Initialized && ent.Comp.Gear != state.Gear)
            _audio.PlayPvs(profile.Shift, ent);
        if (state.Initialized && state.Throttle >= profile.ReleaseFromThrottle &&
            ent.Comp.Throttle <= profile.ReleaseToThrottle && rpm >= profile.ReleaseMinimumRpm &&
            _timing.CurTime >= state.NextRelease)
        {
            // The pair communicates two different mechanisms: exhaust anti-lag and compressor-surge flutter.
            _audio.PlayPvs(profile.Backfire, ent);
            _audio.PlayPvs(profile.ThrottleRelease, ent);
            state.NextRelease = _timing.CurTime + TimeSpan.FromSeconds(profile.ReleaseCooldown);
        }
        state.Throttle = ent.Comp.Throttle;
        state.Gear = ent.Comp.Gear;
        state.Initialized = true;
    }

    private void OnShutdown(Entity<NCVehicleAccessoriesComponent> ent, ref ComponentShutdown args) => Stop(ent);

    private void OnDriveShutdown(Entity<NCVehicleAccessoriesComponent> ent, ref NCVehicleDriveShutdownEvent args)
    {
        if (_prototypes.TryIndex(ent.Comp.SoundProfile, out var profile))
            _audio.PlayPvs(profile.Shutdown, ent);
        Stop(ent);
    }

    private void EnsureLoop(EntityUid vehicle, ref EntityUid? loop, SoundSpecifier? sound, float volume)
    {
        if (sound == null || loop != null && !TerminatingOrDeleted(loop.Value))
            return;
        var played = _audio.PlayPvs(sound, vehicle, AudioParams.Default.WithLoop(true).WithVolume(volume));
        loop = played?.Entity;
    }

    private void UpdateOptionalLoop(EntityUid vehicle, ref EntityUid? loop, SoundSpecifier? sound,
        float weight, NCVehicleSoundProfilePrototype profile)
    {
        if (weight <= 0.01f || sound == null)
        {
            StopLoop(ref loop);
            return;
        }
        EnsureLoop(vehicle, ref loop, sound, profile.MinimumLoopVolume);
        SetLoopVolume(loop, BandVolume(profile, weight));
    }

    private void SetLoopVolume(EntityUid? loop, float volume)
    {
        if (loop != null)
            _audio.SetVolume(loop, volume);
    }

    private static float BandVolume(NCVehicleSoundProfilePrototype profile, float weight)
        => profile.MinimumLoopVolume + (profile.MaximumLoopVolume - profile.MinimumLoopVolume) * weight;

    private static float SmoothStep(float start, float end, float value)
    {
        var amount = Math.Clamp((value - start) / Math.Max(0.001f, end - start), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private void Stop(EntityUid uid)
    {
        if (!_states.Remove(uid, out var state))
            return;
        StopLoop(ref state.Low);
        StopLoop(ref state.Mid);
        StopLoop(ref state.High);
        StopLoop(ref state.ForcedInduction);
        StopLoop(ref state.TyreSlip);
    }

    private void StopLoop(ref EntityUid? loop)
    {
        _audio.Stop(loop);
        loop = null;
    }

    private sealed class VehicleAudioState
    {
        public EntityUid? Low;
        public EntityUid? Mid;
        public EntityUid? High;
        public EntityUid? ForcedInduction;
        public EntityUid? TyreSlip;
        public float Throttle;
        public int Gear;
        public bool Initialized;
        public TimeSpan NextRelease;
    }
}
