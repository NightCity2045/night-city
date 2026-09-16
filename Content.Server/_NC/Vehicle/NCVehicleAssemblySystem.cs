// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared._NC.Vehicle.Workshop;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._NC.Vehicle;

/// <summary>Initializes round-local assemblies and publishes validated derived profiles on demand.</summary>
public sealed partial class NCVehicleAssemblySystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCVehicleComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleWearEvent>(OnWear);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        InitializeMass();
    }

    private void OnStartup(Entity<NCVehicleComponent> ent, ref ComponentStartup args)
    {
        TryRebuild(ent);
    }

    private void OnShutdown(Entity<NCVehicleComponent> ent, ref ComponentShutdown args)
    {
        _massDirty.Remove(ent);
        // Removing the vehicle tag also releases its private round-local assembly.
        RemCompDeferred<NCVehicleAssemblyComponent>(ent);
    }

    public bool TryRebuild(EntityUid uid)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) || TerminatingOrDeleted(uid))
            return false;

        var assembly = EnsureComp<NCVehicleAssemblyComponent>(uid);
        if (!_prototypes.TryIndex(vehicle.Chassis, out var chassis))
        {
            assembly.ProfileValid = false;
            assembly.Profile = default;
            assembly.Failure = new NCVehicleAssemblyFailure(NCVehicleAssemblyError.InvalidChassis);
            RefreshDrive(uid, assembly);
            return false;
        }

        var candidate = assembly.Installed;
        if (!assembly.Initialized)
        {
            candidate = new Dictionary<string, NCVehicleInstalledPart>();
            foreach (var (slotId, slot) in chassis.Slots)
            {
                var part = slot.DefaultPart;
                if (chassis.StockPartOverrides.TryGetValue(slotId, out var stockPart))
                    part = stockPart;
                if (part is { } installedPart)
                    candidate.Add(slotId, new NCVehicleInstalledPart(installedPart, 1));
            }
        }

        // Seed existing round data and derive circuit flags from actual workshop points.
        foreach (var (slotId, state) in candidate.ToArray())
        {
            if (!chassis.Slots.TryGetValue(slotId, out var definition) || definition.WorkPoints.Count == 0)
                continue;
            candidate[slotId] = NormalizeWorkPoints(definition, state);
            assembly.SlotRevisions.TryAdd(slotId, 1);
        }
        if (!NCVehicleAssemblyCompiler.TryCompile(chassis, candidate, ResolvePart, out var profile, out var failure))
        {
            // Retain the installed graph for inspection, but never expose a stale valid physics profile.
            assembly.ProfileValid = false;
            assembly.Profile = default;
            assembly.Failure = failure;
            RefreshDrive(uid, assembly);
            return false;
        }

        assembly.Installed = candidate;
        assembly.Profile = profile;
        assembly.Failure = default;
        assembly.Initialized = true;
        assembly.ProfileValid = true;
        assembly.Revision++;
        RefreshTransmission(uid, chassis, assembly);
        RefreshDrive(uid, assembly);
        return true;
    }

    private void RefreshTransmission(EntityUid uid, NCVehicleChassisPrototype chassis, NCVehicleAssemblyComponent assembly)
    {
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive) || chassis.ManualHandling == null)
            return;
        var automatic = false;
        var allWheel = false;
        foreach (var part in assembly.Installed.Values)
        {
            if (!_prototypes.TryIndex(part.Prototype, out var definition))
                continue;
            // Hardware defines the layout even when disconnected; its circuit separately gates delivered torque.
            automatic |= definition.AutomaticTransmission;
            allWheel |= definition.AllWheelDrive;
        }
        var selected = automatic ? allWheel ? chassis.AutomaticAllWheelHandling : chassis.AutomaticHandling :
            allWheel ? chassis.AllWheelHandling : chassis.ManualHandling;
        if (selected is not { } handling || handling == drive.Handling || !_prototypes.TryIndex(handling, out var tuning))
            return;
        drive.Handling = handling;
        drive.Gear = 0;
        drive.LeverRow = 0;
        drive.PendingShift = null;
        drive.ShiftStatus = NCVehicleShiftStatus.None;
        drive.Selector = NCVehicleSelector.Neutral;
        drive.WheelSpeeds = new float[tuning.Wheels.Count];
        drive.EngineRunning = false;
        Dirty(uid, drive);
    }

    private void RefreshDrive(EntityUid uid, NCVehicleAssemblyComponent assembly)
    {
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive))
            return;
        RefreshPerformance(uid, assembly, drive);
        if (assembly.ProfileValid)
        {
            var profile = CompileLoadedProfile(uid, assembly.Profile);
            assembly.LoadedProfile = profile;
            drive.AssemblyCenter = profile.CenterOfMass;
            drive.AssemblyInertia = profile.Inertia;
            if (TryComp<PhysicsComponent>(uid, out var body) && body.Mass > 0 &&
                TryComp<FixturesComponent>(uid, out var fixtures))
            {
                // Replicate density through the engine's fixture state. The derived COM is in drive state.
                var ratio = profile.Mass / body.Mass;
                foreach (var (id, fixture) in fixtures.Fixtures)
                    _physics.SetDensity(uid, id, fixture, fixture.Density * ratio, manager: fixtures);
                _physics.ResetMassData(uid, fixtures, body);
                _physics.SetLocalCenter(uid, body, profile.CenterOfMass);
                _physics.SetInertia(uid, body, profile.Inertia + profile.Mass * profile.CenterOfMass.LengthSquared());
            }
        }
        Dirty(uid, drive);
    }

    public bool TryGetProfile(EntityUid uid, out NCVehicleAssemblyProfile profile)
    {
        profile = default;
        if (TerminatingOrDeleted(uid) || !HasComp<NCVehicleComponent>(uid) ||
            !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) || !assembly.ProfileValid)
            return false;
        profile = assembly.LoadedProfile;
        return true;
    }

    public bool CanSetPart(EntityUid uid, string slot, NCVehicleInstalledPart? part)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) ||
            !_prototypes.TryIndex(vehicle.Chassis, out var chassis) || !chassis.Slots.ContainsKey(slot) ||
            !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly))
            return false;
        var candidate = new Dictionary<string, NCVehicleInstalledPart>(assembly.Installed);
        if (part is { } installed)
            candidate[slot] = installed;
        else
            candidate.Remove(slot);
        return NCVehicleAssemblyCompiler.TryCompile(chassis, candidate, ResolvePart, out _, out _);
    }

    /// <summary>Validate a complete transfer before moving any physical item or changing its support graph.</summary>
    public bool CanSetParts(EntityUid uid, IReadOnlyDictionary<string, NCVehicleInstalledPart?> changes)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) ||
            !_prototypes.TryIndex(vehicle.Chassis, out var chassis) ||
            !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) || changes.Count == 0)
            return false;
        var candidate = new Dictionary<string, NCVehicleInstalledPart>(assembly.Installed);
        foreach (var (slot, part) in changes)
        {
            if (!chassis.Slots.ContainsKey(slot)) return false;
            if (part is { } installed) candidate[slot] = installed;
            else candidate.Remove(slot);
        }
        return NCVehicleAssemblyCompiler.TryCompile(chassis, candidate, ResolvePart, out _, out _);
    }

    public bool TrySetParts(EntityUid uid, IReadOnlyDictionary<string, NCVehicleInstalledPart?> changes)
    {
        if (!CanSetParts(uid, changes)) return false;
        var assembly = Comp<NCVehicleAssemblyComponent>(uid);
        var candidate = new Dictionary<string, NCVehicleInstalledPart>(assembly.Installed);
        foreach (var (slot, part) in changes)
        {
            var charged = TransferBatteryCharge(uid, slot, part, assembly);
            if (charged is { } installed) candidate[slot] = installed;
            else candidate.Remove(slot);
            assembly.DiagnosedSlots.Remove(slot);
            assembly.InspectedConditions.Remove(slot);
            assembly.PreciselyDiagnosedSlots.Remove(slot);
            assembly.SlotRevisions[slot] = assembly.SlotRevisions.GetValueOrDefault(slot) + 1;
        }
        // Compile once: no intermediate missing support, duplicated wheel mass, or observer snapshots.
        assembly.Installed = candidate;
        var result = TryRebuild(uid);
        var changed = new NCVehicleAssemblyChangedEvent(string.Empty);
        RaiseLocalEvent(uid, ref changed);
        return result;
    }

    public bool TrySetPart(EntityUid uid, string slot, NCVehicleInstalledPart? part)
    {
        if (!CanSetPart(uid, slot, part))
            return false;
        var assembly = Comp<NCVehicleAssemblyComponent>(uid);
        // Trusted legacy system calls may still set a whole circuit. Translate that request once;
        // point-level callers supply a fresh dictionary and cannot accidentally toggle the entire circuit.
        if (part is { } next && assembly.Installed.TryGetValue(slot, out var current) &&
            ReferenceEquals(next.WorkPoints, current.WorkPoints) && next.WorkPoints != null &&
            (next.Connected != current.Connected || next.Fastened != current.Fastened))
        {
            var points = new Dictionary<string, bool>(next.WorkPoints);
            var definition = _prototypes.Index(Comp<NCVehicleComponent>(uid).Chassis).Slots[slot];
            foreach (var (id, point) in definition.WorkPoints)
            {
                if (point.Kind == NCVehicleWorkshopPointKind.Fastener && next.Fastened != current.Fastened)
                    points[id] = next.Fastened;
                if (point.Kind == NCVehicleWorkshopPointKind.Connector && next.Connected != current.Connected)
                    points[id] = next.Connected;
            }
            part = next with { WorkPoints = points };
        }
        part = TransferBatteryCharge(uid, slot, part, assembly);
        if (part == null || !assembly.Installed.TryGetValue(slot, out var previous) ||
            previous.Prototype != part.Value.Prototype)
        {
            assembly.DiagnosedSlots.Remove(slot);
            assembly.InspectedConditions.Remove(slot);
            assembly.PreciselyDiagnosedSlots.Remove(slot);
        }
        if (part is { } installed)
            assembly.Installed[slot] = installed;
        else
            assembly.Installed.Remove(slot);
        if (part is { Condition: > 0 } && TryComp<NCVehicleBodyComponent>(uid, out var shell) &&
            _prototypes.Index(Comp<NCVehicleComponent>(uid).Chassis).Slots[slot].Functions.Contains(NCVehicleFunction.Airbags))
        {
            shell.AirbagsDeployed = false;
            Dirty(uid, shell);
        }
        assembly.SlotRevisions[slot] = assembly.SlotRevisions.GetValueOrDefault(slot) + 1;
        var rebuilt = TryRebuild(uid);
        var changed = new NCVehicleAssemblyChangedEvent(slot);
        RaiseLocalEvent(uid, ref changed);
        return rebuilt;
    }

    private static NCVehicleInstalledPart NormalizeWorkPoints(NCVehicleSlotDefinition definition, NCVehicleInstalledPart state)
    {
        var points = new Dictionary<string, bool>();
        var torque = new Dictionary<string, float>();
        var fastened = true;
        var connected = true;
        foreach (var (id, point) in definition.WorkPoints)
        {
            var attached = state.WorkPoints?.GetValueOrDefault(id) ??
                (point.Kind == NCVehicleWorkshopPointKind.Fastener ? state.Fastened : state.Connected);
            points[id] = attached;
            if (point.Kind == NCVehicleWorkshopPointKind.Fastener)
            {
                var target = point.TargetTorque > 0 ? point.TargetTorque : definition.FastenerTorque;
                var applied = state.PointTorque?.GetValueOrDefault(id) ?? (attached ? target : 0);
                torque[id] = applied;
                fastened &= attached && (target <= 0 ||
                    MathF.Abs(applied - target) <= target * point.TorqueTolerance);
            }
            else
                connected &= attached;
        }
        return state with { WorkPoints = points, PointTorque = torque, Connected = connected, Fastened = fastened };
    }

    private NCVehicleInstalledPart? TransferBatteryCharge(EntityUid uid, string slot,
        NCVehicleInstalledPart? next, NCVehicleAssemblyComponent assembly)
    {
        if (!TryComp<NCVehicleConditionComponent>(uid, out var condition))
            return next;
        var hadBattery = assembly.Installed.TryGetValue(slot, out var old) &&
            _prototypes.Index(old.Prototype).BatteryCapacity > 0;
        var newDefinition = next is { } replacement ? _prototypes.Index(replacement.Prototype) : null;
        if (!hadBattery && (newDefinition == null || newDefinition.BatteryCapacity <= 0))
            return next;
        if (next is { } battery && newDefinition is { BatteryCapacity: > 0 })
        {
            // Disconnecting stores the live charge; reconnecting consumes that same stored state.
            var charge = hadBattery && old.Connected && old.Fastened ? condition.BatteryCharge :
                battery.StoredCharge ?? newDefinition.InitialCharge;
            next = battery with { StoredCharge = Math.Clamp(charge, 0, newDefinition.BatteryCapacity) };
            condition.BatteryCapacity = newDefinition.BatteryCapacity;
            condition.BatteryCharge = battery.Connected && battery.Fastened ? next.Value.StoredCharge!.Value : 0;
        }
        else
            condition.BatteryCharge = 0;
        Dirty(uid, condition);
        return next;
    }

    private void OnWear(Entity<NCVehicleComponent> ent, ref NCVehicleWearEvent args)
    {
        if (!float.IsFinite(args.Engine) || !float.IsFinite(args.Gearbox) || args.Engine < 0 || args.Gearbox < 0 ||
            !TryComp<NCVehicleAssemblyComponent>(ent, out var assembly) || !TryComp<NCVehicleDriveComponent>(ent, out var drive))
            return;
        var changed = false;
        var chassis = _prototypes.Index(ent.Comp.Chassis);
        // A bounded maintenance pulse changes condition only; mass compilation is not needed for wear.
        foreach (var (slot, part) in assembly.Installed.ToArray())
        {
            if (!part.Connected || !part.Fastened || !_prototypes.TryIndex(part.Prototype, out var definition))
                continue;
            var wear = args.Engine * definition.EngineWearShare + args.Gearbox * definition.GearboxWearShare;
            if (chassis.Slots.TryGetValue(slot, out var mount) && mount.WheelIndex >= 0 &&
                mount.WheelIndex < drive.WheelWear.Length)
                wear += drive.WheelWear[mount.WheelIndex] * definition.WheelWearShare;
            if (wear <= 0 || !float.IsFinite(wear))
                continue;
            assembly.Installed[slot] = part with { Condition = Math.Max(0, part.Condition - wear) };
            assembly.SlotRevisions[slot] = assembly.SlotRevisions.GetValueOrDefault(slot) + 1;
            changed = true;
        }
        Array.Clear(drive.WheelWear);
        if (!changed)
            return;
        assembly.Revision++;
        RefreshPerformance(ent, assembly, drive);
        Dirty(ent.Owner, drive);
        var updated = new NCVehicleAssemblyChangedEvent(string.Empty);
        RaiseLocalEvent(ent.Owner, ref updated);
    }

    private void RefreshPerformance(EntityUid uid, NCVehicleAssemblyComponent assembly, NCVehicleDriveComponent drive)
    {
        // Wear can destroy a required module without changing its mass. Revalidate start permission here,
        // so an ignition request cannot restart an assembly between wear pulses.
        drive.AssemblyReady = assembly.ProfileValid && assembly.Profile.RequiredSlotsFilled && assembly.Lift == null && !drive.Overturned;
        if (TryComp<NCVehicleComponent>(uid, out var vehicle) && _prototypes.TryIndex(vehicle.Chassis, out var chassis))
            foreach (var (slot, definition) in chassis.Slots)
                if (definition.Required && (!assembly.Installed.TryGetValue(slot, out var installed) ||
                    !installed.Connected || !installed.Fastened || installed.Condition <= 0))
                    drive.AssemblyReady = false;
        drive.AssemblyPower = 1;
        if (TryComp<NCVehicleComponent>(uid, out var identity) &&
            _prototypes.TryIndex(identity.Chassis, out var definitionChassis) &&
            _prototypes.TryIndex(drive.Handling, out var handling))
        {
            drive.AssemblyFunctions = NCVehiclePerformanceCompiler.Compile(definitionChassis,
                assembly.Installed, ResolvePart, handling.Wheels.Count);
            if (drive.WheelWear.Length != handling.Wheels.Count)
                drive.WheelWear = new float[handling.Wheels.Count];
        }
        foreach (var (_, part) in assembly.Installed)
        {
            if (!part.Connected || !part.Fastened || !_prototypes.TryIndex(part.Prototype, out var definition))
                continue;
            drive.AssemblyPower *= definition.PowerMultiplier * (definition.PowerDependsOnCondition ? part.Condition : 1);
        }
        if (!float.IsFinite(drive.AssemblyPower) || drive.AssemblyPower <= 0)
        {
            drive.AssemblyPower = 0;
            drive.EngineRunning = false;
        }
        if (!drive.AssemblyReady)
            drive.EngineRunning = false;
    }

    private NCVehiclePartPrototype? ResolvePart(ProtoId<NCVehiclePartPrototype> id)
    {
        return _prototypes.TryIndex(id, out var part) ? part : null;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCVehicleChassisPrototype>() && !args.WasModified<NCVehiclePartPrototype>())
            return;

        // This is an explicit content-reload pass, not a per-tick entity scan. Empty slots stay empty.
        var query = EntityQueryEnumerator<NCVehicleComponent>();
        while (query.MoveNext(out var uid, out _))
            TryRebuild(uid);
    }
}
