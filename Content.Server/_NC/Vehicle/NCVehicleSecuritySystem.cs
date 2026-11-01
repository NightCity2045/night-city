// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Audio;
using Content.Shared.Damage.Systems;
using Content.Shared.Interaction;
using Content.Shared.Lock;
using Content.Shared.Verbs;
using Robust.Shared.Timing;

namespace Content.Server._NC.Vehicle;

/// <summary>Real alarm hardware. Manual key access and alarm fallback do not depend on a META process.</summary>
[RegisterComponent]
public sealed partial class NCVehicleSecurityComponent : Component
{
    [DataField] public float AlarmSeconds = 15;
    [DataField] public float TriggerCooldownSeconds = 5;
    [DataField] public float ArmedWatts = 0.1f;
    [DataField] public float AlarmWatts = 12;
    public bool Armed;
    public bool AlarmActive;
    public bool? PendingArmed;
    public EntityUid? PendingActor;
    public uint KeyGeneration;
    public TimeSpan PendingUntil;
    public TimeSpan AlarmUntil;
    public TimeSpan NextTrigger;
}

public sealed partial class NCVehicleSecuritySystem : EntitySystem
{
    [Dependency] private LockSystem _locks = default!;
    [Dependency] private NCVehicleAccessSystem _access = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private NCVehicleConditionSystem _condition = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, NCVehicleSecurityComponent> _devices = [];
    private TimeSpan _nextSample;
    private TimeSpan _lastSample;
    public event Action<EntityUid>? StateChanged;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleSecurityComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCVehicleSecurityComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCVehicleSecurityComponent, ActivateInWorldEvent>(OnActivate, after: [typeof(LockSystem)]);
        SubscribeLocalEvent<NCVehicleSecurityComponent, LockToggledEvent>(OnLockToggled);
        SubscribeLocalEvent<NCVehicleSecurityComponent, DamageChangedEvent>(OnDamage);
        SubscribeLocalEvent<NCVehicleSecurityComponent, NCVehicleTheftDoAfterEvent>(OnTheft, after: [typeof(NCVehicleOwnershipSystem)]);
        SubscribeLocalEvent<NCVehicleSecurityComponent, NCVehicleAssemblyChangedEvent>(OnAssembly);
        SubscribeLocalEvent<NCVehicleSecurityComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
    }
    private void OnStartup(Entity<NCVehicleSecurityComponent> ent, ref ComponentStartup args)
    { _devices[ent.Owner] = ent.Comp; _ambient.SetAmbience(ent.Owner, false); }
    private void OnShutdown(Entity<NCVehicleSecurityComponent> ent, ref ComponentShutdown args)
    { _ambient.SetAmbience(ent.Owner, false); _devices.Remove(ent.Owner); }
    private void OnVerbs(Entity<NCVehicleSecurityComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !CanSetArmed(ent.Owner, args.User)) return;
        var user = args.User; var armed = ent.Comp.Armed;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(armed ? "nc-vehicle-security-disarm" : "nc-vehicle-security-arm"),
            Act = () => TrySetArmed(ent.Owner, user, !armed) });
    }
    public bool HasHardware(EntityUid uid) => HasComp<NCVehicleSecurityComponent>(uid) && HasComp<LockComponent>(uid) &&
        HasComp<AmbientSoundComponent>(uid) && TryComp<NCVehicleAccessComponent>(uid, out var access) && access.Registered;
    private bool HasPower(EntityUid uid) => TryComp<NCVehicleConditionComponent>(uid, out var condition) && float.IsFinite(condition.BatteryCharge) && condition.BatteryCharge > 0 &&
        TryComp<NCVehicleDriveComponent>(uid, out var drive) && NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Electronics) > 0;
    public bool CanSetArmed(EntityUid uid, EntityUid user) => HasHardware(uid) && HasPower(uid) &&
        _blocker.CanConsciouslyPerformAction(user) && _access.HasKey(uid, user) && _interaction.InRangeUnobstructed(user, uid, 3);
    public bool TrySetArmed(EntityUid uid, EntityUid user, bool armed)
    {
        if (!CanSetArmed(uid, user) || !TryComp<NCVehicleSecurityComponent>(uid, out var security) ||
            !TryComp<LockComponent>(uid, out var physicalLock) || !TryComp<NCVehicleAccessComponent>(uid, out var access)) return false;
        if (security.PendingArmed != null) return false;
        if (physicalLock.Locked == armed)
        {
            security.Armed = armed; security.KeyGeneration = access.KeyGeneration;
            if (!armed) StopAlarm(uid, security);
            StateChanged?.Invoke(uid); return true;
        }
        security.PendingArmed = armed; security.PendingActor = user; security.KeyGeneration = access.KeyGeneration;
        security.PendingUntil = _timing.CurTime + (armed ? physicalLock.LockTime : physicalLock.UnlockTime) + TimeSpan.FromSeconds(1);
        var accepted = armed ? _locks.TryLock(uid, user, physicalLock) : _locks.TryUnlock(uid, user, physicalLock);
        if (!accepted) { security.PendingArmed = null; security.PendingActor = null; }
        StateChanged?.Invoke(uid); return accepted;
    }
    private void OnLockToggled(Entity<NCVehicleSecurityComponent> ent, ref LockToggledEvent args)
    {
        if (!args.Locked) { ent.Comp.Armed = false; StopAlarm(ent.Owner, ent.Comp); }
        else if (ent.Comp.PendingArmed == true && ent.Comp.PendingActor is { } actor && CanSetArmed(ent.Owner, actor) &&
            TryComp<NCVehicleAccessComponent>(ent.Owner, out var access) && access.KeyGeneration == ent.Comp.KeyGeneration)
            ent.Comp.Armed = true;
        ent.Comp.PendingArmed = null; ent.Comp.PendingActor = null; StateChanged?.Invoke(ent.Owner);
    }
    private void OnActivate(Entity<NCVehicleSecurityComponent> ent, ref ActivateInWorldEvent args)
    { if (args.Handled && args.Complex && !_access.HasKey(ent.Owner, args.User)) Trigger(ent.Owner, ent.Comp); }
    private void OnDamage(Entity<NCVehicleSecurityComponent> ent, ref DamageChangedEvent args)
    { if (args.DamageIncreased) Trigger(ent.Owner, ent.Comp); }
    private void OnTheft(Entity<NCVehicleSecurityComponent> ent, ref NCVehicleTheftDoAfterEvent args)
    { if (!args.Cancelled && args.Handled) Trigger(ent.Owner, ent.Comp); }
    private void OnAssembly(Entity<NCVehicleSecurityComponent> ent, ref NCVehicleAssemblyChangedEvent args)
    { if (!HasPower(ent.Owner)) StopAlarm(ent.Owner, ent.Comp); }
    private void Trigger(EntityUid uid, NCVehicleSecurityComponent state)
    {
        if (!state.Armed || !HasPower(uid) || _timing.CurTime < state.NextTrigger || !float.IsFinite(state.AlarmSeconds) ||
            state.AlarmSeconds is <= 0 or > 60 || !float.IsFinite(state.TriggerCooldownSeconds) || state.TriggerCooldownSeconds is < 1 or > 60) return;
        state.AlarmActive = true; state.AlarmUntil = _timing.CurTime + TimeSpan.FromSeconds(state.AlarmSeconds);
        state.NextTrigger = _timing.CurTime + TimeSpan.FromSeconds(state.TriggerCooldownSeconds);
        _ambient.SetAmbience(uid, true); StateChanged?.Invoke(uid);
    }
    private void StopAlarm(EntityUid uid, NCVehicleSecurityComponent state)
    { state.AlarmActive = false; _ambient.SetAmbience(uid, false); }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextSample) return;
        var seconds = _lastSample == TimeSpan.Zero ? 0 : Math.Max(0, (float) (_timing.CurTime - _lastSample).TotalSeconds);
        _lastSample = _timing.CurTime;
        _nextSample = _timing.CurTime + TimeSpan.FromMilliseconds(250);
        foreach (var (uid, state) in _devices)
        {
            if (IsPaused(uid)) continue;
            var changed = false;
            var watts = (state.Armed ? state.ArmedWatts : 0) + (state.AlarmActive ? state.AlarmWatts : 0);
            if (seconds > 0 && float.IsFinite(watts) && watts > 0) _condition.TryUseElectricalEnergy(uid, watts * seconds);
            if (state.PendingArmed != null && _timing.CurTime >= state.PendingUntil)
            { state.PendingArmed = null; state.PendingActor = null; changed = true; }
            if (state.Armed && (!TryComp<NCVehicleAccessComponent>(uid, out var access) || access.KeyGeneration != state.KeyGeneration))
            { state.Armed = false; StopAlarm(uid, state); changed = true; }
            if (state.AlarmActive && (_timing.CurTime >= state.AlarmUntil || !HasPower(uid)))
            { StopAlarm(uid, state); changed = true; }
            if (changed) StateChanged?.Invoke(uid);
        }
    }
}
