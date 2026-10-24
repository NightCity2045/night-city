// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;
using Content.Shared.Emp;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Cyberware;

/// <summary>
/// Owns physical implant placement and deterministic power, heat, EMP and neural-interface state for NC humans.
/// Installed modules are scheduled directly from organ/container events; no entity or world query is performed.
/// </summary>
public sealed partial class NCCyberwareSystem : EntitySystem
{
    private const int MaximumCyberwarePerBody = 64;

    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private OrganRelationSystem _relations = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private Content.Server._NC.Medical.Humanity.NCHumanitySystem _humanity = default!;

    private readonly Dictionary<EntityUid, TimeSpan> _scheduled = [];
    private readonly PriorityQueue<EntityUid, long> _schedule = new();
    private readonly HashSet<ProtoId<NCCyberwarePrototype>> _validatedProfiles = [];
    private readonly HashSet<ProtoId<NCCyberwareSlotPrototype>> _validatedSlots = [];
    private readonly HashSet<ProtoId<NCCyberwareRepairProfilePrototype>> _validatedRepairProfiles = [];

    public override void Initialize()
    {
        base.Initialize();
        InitializeAssemblyEvents();
        InitializeWorkbenchEvents();
        InitializePowerEvents();
        InitializePowerServiceEvents();
        InitializeWorldPersistence();

        SubscribeLocalEvent<NCCyberwareSlotHostComponent, ComponentInit>(OnSlotHostInit);
        SubscribeLocalEvent<NCCyberwareSlotHostComponent, ComponentShutdown>(OnSlotHostShutdown);
        SubscribeLocalEvent<NCCyberwareComponent, MapInitEvent>(OnCyberwareMapInit);
        SubscribeLocalEvent<NCCyberwareComponent, ComponentShutdown>(OnCyberwareShutdown);
        SubscribeLocalEvent<NCCyberwareComponent, OrganGotInsertedEvent>(OnCyberwareOrganInserted);
        SubscribeLocalEvent<NCCyberwareComponent, OrganGotRemovedEvent>(OnCyberwareOrganRemoved);
        SubscribeLocalEvent<BodyComponent, OrganInsertedIntoEvent>(OnBodyOrganInserted);
        SubscribeLocalEvent<BodyComponent, OrganRemovedFromEvent>(OnBodyOrganRemoved);
        SubscribeLocalEvent<NCMedicalBodyComponent, EmpPulseEvent>(OnEmpPulse,
            before: [typeof(Content.Shared.Power.EntitySystems.SharedBatterySystem)]);
    }

    private void OnSlotHostInit(Entity<NCCyberwareSlotHostComponent> host, ref ComponentInit args)
    {
        host.Comp.Modules = _containers.EnsureContainer<Container>(host, NCCyberwareSlotHostComponent.ContainerId);
        ValidateHostSlots(host.Owner, host.Comp);
    }

    private void OnSlotHostShutdown(Entity<NCCyberwareSlotHostComponent> host, ref ComponentShutdown args)
    {
        if (host.Comp.Modules is { } modules)
            _containers.ShutdownContainer(modules);
    }

    private void OnCyberwareMapInit(Entity<NCCyberwareComponent> module, ref MapInitEvent args)
    {
        if (!TryGetProfile(module.Comp.Profile, out var profile))
            return;

        InitializeState(module.Owner, module.Comp, profile);
        if (TryResolveOwningBody(module.Owner, out _))
            Schedule(module.Owner, profile, true);
    }

    private void OnCyberwareShutdown(Entity<NCCyberwareComponent> module, ref ComponentShutdown args)
    {
        _physicalIdentities.Remove(module.Owner);
        _scheduled.Remove(module.Owner);
    }

    private void OnCyberwareOrganInserted(Entity<NCCyberwareComponent> module, ref OrganGotInsertedEvent args)
    {
        if (TryGetProfile(module.Comp.Profile, out var profile))
            Schedule(module.Owner, profile, true);
    }

    private void OnCyberwareOrganRemoved(Entity<NCCyberwareComponent> module, ref OrganGotRemovedEvent args)
    {
        module.Comp.CrisisEfficiency = 1f;
        module.Comp.CrisisBlocksActiveModes = false;
        _scheduled.Remove(module.Owner);
    }

    private void OnBodyOrganInserted(Entity<BodyComponent> body, ref OrganInsertedIntoEvent args)
    {
        if (!HasComp<NCMedicalBodyComponent>(body.Owner))
            return;
        ScheduleContainedModules(args.Organ, true);
    }

    private void OnBodyOrganRemoved(Entity<BodyComponent> body, ref OrganRemovedFromEvent args)
    {
        if (!HasComp<NCMedicalBodyComponent>(body.Owner))
            return;
        ScheduleContainedModules(args.Organ, false);
        RefreshBodyPower(body, _timing.CurTime);
    }

    private void OnEmpPulse(Entity<NCMedicalBodyComponent> body, ref EmpPulseEvent args)
    {
        if (!float.IsFinite(args.EnergyConsumption) || args.EnergyConsumption <= 0f || args.Duration <= TimeSpan.Zero)
            return;

        var result = ApplyEmp(new NCCyberwareEmpRequest(
            args.PulseId == Guid.Empty ? NCActionResolutionId.New() : new NCActionResolutionId(args.PulseId),
            body.Owner,
            args.EnergyConsumption,
            args.Duration));
        if (result.Accepted && result.AffectedModules > 0)
            args.Affected = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        while (_schedule.TryPeek(out var body, out var dueTicks) && dueTicks <= now.Ticks)
        {
            _schedule.Dequeue();
            if (!_scheduled.TryGetValue(body, out var expectedDue) || expectedDue.Ticks != dueTicks)
                continue;
            _scheduled.Remove(body);
            if (!HasComp<NCMedicalBodyComponent>(body) || TerminatingOrDeleted(body))
                continue;
            TryAdvanceBodyPower(body, now);
            SchedulePowerBody(body);
        }
    }

    /// <summary>
    /// Read-only surgical preflight for a physical implant and one live host organ. Slot choice is stable by
    /// prototype ID and is repeated by <see cref="TryInstall"/> after the timed surgery step completes.
    /// </summary>
    public bool TryFindInstallSlot(
        EntityUid patient,
        EntityUid hostOrgan,
        EntityUid moduleUid,
        out ProtoId<NCCyberwareSlotPrototype> slotId)
    {
        slotId = default;
        if (!TryComp<NCMedicalBodyComponent>(patient, out _) ||
            !TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryGetProfile(module.Profile, out var profile) ||
            profile.Form != NCCyberwareForm.Implant ||
            module.InstalledSlot != null ||
            TryComp<ChildOrganComponent>(moduleUid, out var child) && child.Parent != null ||
            TryComp<OrganComponent>(moduleUid, out var organ) && organ.Body != null ||
            !TryResolveHost(patient, hostOrgan, out var host, out var hostCategory) ||
            host.Modules == null)
        {
            return false;
        }

        foreach (var candidate in host.Slots.OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            if (!_prototypes.TryIndex(candidate, out NCCyberwareSlotPrototype? slot) ||
                !slot.HostCategories.Contains(hostCategory) ||
                !profile.CompatibleSlots.Contains(candidate) ||
                profile.Size > slot.Capacity ||
                FindInstalledInSlot(host, candidate) != null)
            {
                continue;
            }

            slotId = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves one installed module in a live host without scanning the world. Profile and stable instance
    /// identity provide deterministic ordering when a future host exposes more than one populated slot.
    /// </summary>
    public bool TryFindInstalledModule(
        EntityUid patient,
        EntityUid hostOrgan,
        IReadOnlyCollection<ProtoId<NCCyberwarePrototype>> compatibleProfiles,
        out EntityUid moduleUid)
    {
        moduleUid = default;
        if (!TryResolveHost(patient, hostOrgan, out var host, out _) || host.Modules == null)
            return false;

        var candidates = new List<(EntityUid Uid, string Profile, Guid StableId)>();
        foreach (var contained in host.Modules.ContainedEntities)
        {
            if (!TryComp<NCCyberwareComponent>(contained, out var module) ||
                module.InstalledSlot == null ||
                compatibleProfiles.Count > 0 && !compatibleProfiles.Contains(module.Profile))
            {
                continue;
            }

            candidates.Add((contained, module.Profile.Id, module.StableInstanceId));
        }

        if (candidates.Count == 0)
            return false;

        moduleUid = candidates
            .OrderBy(value => value.Profile, StringComparer.Ordinal)
            .ThenBy(value => value.StableId)
            .ThenBy(value => value.Uid.Id)
            .First().Uid;
        return true;
    }

    /// <summary>
    /// Surgery-facing installation boundary. The caller supplies only entities already validated by its DoAfter;
    /// this method revalidates the live body graph and performs one idempotent physical transition.
    /// </summary>
    public NCCyberwareMutationResult TryInstall(in NCCyberwareInstallRequest request)
    {
        if (!request.ResolutionId.IsValid)
            return Reject(NCCyberwareMutationFailure.InvalidResolution);
        if (!TryComp<NCMedicalBodyComponent>(request.Patient, out _))
            return Reject(NCCyberwareMutationFailure.PatientNotAuthoritative);

        var key = new NCCyberwareProcessedActionKey(
            request.ResolutionId.Value,
            NCCyberwareActionKind.Install,
            0,
            0);
        if (IsDuplicate(request.Patient, key))
            return Duplicate();

        if (!TryComp<NCCyberwareComponent>(request.Module, out var module) ||
            !TryGetProfile(module.Profile, out var profile))
        {
            return Reject(NCCyberwareMutationFailure.ModuleUnavailable);
        }

        if (profile.Form != NCCyberwareForm.Implant)
            return Reject(NCCyberwareMutationFailure.SlotIncompatible);
        if (module.InstalledSlot != null ||
            TryComp<ChildOrganComponent>(request.Module, out var existingChild) && existingChild.Parent != null ||
            TryComp<OrganComponent>(request.Module, out var moduleOrgan) && moduleOrgan.Body != null)
        {
            return Reject(NCCyberwareMutationFailure.AlreadyInstalled);
        }

        if (!TryResolveHost(request.Patient, request.HostOrgan, out var host, out var hostCategory))
            return Reject(NCCyberwareMutationFailure.HostNotInPatient);
        if (!host.Slots.Contains(request.Slot) || host.Modules == null ||
            !_prototypes.TryIndex(request.Slot, out NCCyberwareSlotPrototype? slot))
        {
            return Reject(NCCyberwareMutationFailure.HostSlotUnavailable);
        }

        ValidateSlot(slot);
        if (!slot.HostCategories.Contains(hostCategory) || !profile.CompatibleSlots.Contains(request.Slot))
            return Reject(NCCyberwareMutationFailure.SlotIncompatible);
        if (profile.Size > slot.Capacity)
            return Reject(NCCyberwareMutationFailure.CapacityExceeded);
        if (FindInstalledInSlot(host, request.Slot) != null)
            return Reject(NCCyberwareMutationFailure.SlotOccupied);
        if (!TryComp<ParentOrganComponent>(request.HostOrgan, out var parent) ||
            !TryComp<ChildOrganComponent>(request.Module, out var child))
        {
            return Reject(NCCyberwareMutationFailure.SlotIncompatible);
        }

        TryAdvanceBodyPower(request.Patient, _timing.CurTime);
        InitializeState(request.Module, module, profile);
        _humanity.TrySettleBody(request.Patient);
        if (!_containers.Insert(request.Module, host.Modules, containerXform: Transform(request.HostOrgan)))
            return Reject(NCCyberwareMutationFailure.PhysicalContainerFailure);

        _relations.Relate((request.HostOrgan, parent), (request.Module, child));
        module.InstalledSlot = request.Slot;
        ApplyRequestedNeuralConnection(
            module,
            profile,
            request.ConnectNeuralInterface,
            request.CalibrateNeuralInterface);
        if (module.StableInstanceId == Guid.Empty)
            module.StableInstanceId = request.ResolutionId.DeriveGuid(0x43594245U);

        UpdateOperational(request.Module, module, profile, _timing.CurTime);
        Remember(request.Patient, key);
        Schedule(request.Module, profile, true);
        RecordService(request.Module, NCCyberwareServiceOperation.Installed, request.ResolutionId.Value, request.Patient);
        MarkBodyChanged(request.Patient);
        var installed = new NCCyberwareInstalledEvent(
            request.ResolutionId,
            request.Module,
            request.HostOrgan,
            request.Slot);
        RaiseLocalEvent(request.Patient, ref installed);
        RaiseStateChanged(
            request.Patient,
            request.Module,
            request.ResolutionId,
            NCCyberwareStateChangeKind.Installed,
            0f,
            module.FunctionalEfficiency);
        return Applied(1);
    }

    /// <summary>
    /// Surgery-facing removal boundary. Removed hardware is ejected beside the patient and remains a physical
    /// entity carrying its damage, charge, heat and stable manufacturing identity.
    /// </summary>
    public NCCyberwareMutationResult TryRemove(in NCCyberwareRemoveRequest request)
    {
        if (!request.ResolutionId.IsValid)
            return Reject(NCCyberwareMutationFailure.InvalidResolution);
        if (!TryComp<NCMedicalBodyComponent>(request.Patient, out _))
            return Reject(NCCyberwareMutationFailure.PatientNotAuthoritative);

        var key = new NCCyberwareProcessedActionKey(
            request.ResolutionId.Value,
            NCCyberwareActionKind.Remove,
            0,
            0);
        if (IsDuplicate(request.Patient, key))
            return Duplicate();

        if (!TryResolveInstalledModule(
                request.Patient,
                request.Module,
                out var module,
                out var hostUid,
                out var host,
                out var slot))
        {
            return Reject(NCCyberwareMutationFailure.NotInstalledInPatient);
        }

        if (!TryComp<ChildOrganComponent>(request.Module, out var child) ||
            !TryComp<ParentOrganComponent>(hostUid, out var parent) ||
            host.Modules == null)
        {
            return Reject(NCCyberwareMutationFailure.NotInstalledInPatient);
        }

        TryAdvanceBodyPower(request.Patient, _timing.CurTime);
        _humanity.TrySettleBody(request.Patient);
        _relations.Orphan((request.Module, child));
        if (!_containers.Remove(request.Module, host.Modules, destination: Transform(request.Patient).Coordinates))
        {
            _relations.Relate((hostUid, parent), (request.Module, child));
            return Reject(NCCyberwareMutationFailure.PhysicalContainerFailure);
        }

        var previousFunction = module.FunctionalEfficiency;
        module.InstalledSlot = null;
        InvalidateCalibration(request.Module);
        module.Load = 0f;
        module.Operational = false;
        module.FunctionalEfficiency = 0f;
        if (module.NeuralState != NCCyberwareNeuralState.NotApplicable)
            module.NeuralState = NCCyberwareNeuralState.Disconnected;
        _scheduled.Remove(request.Module);
        RefreshBodyPower(request.Patient, _timing.CurTime);
        Remember(request.Patient, key);
        RecordService(request.Module, NCCyberwareServiceOperation.Removed, request.ResolutionId.Value, request.Patient);
        MarkBodyChanged(request.Patient);
        var removed = new NCCyberwareRemovedEvent(
            request.ResolutionId,
            request.Module,
            hostUid,
            slot);
        RaiseLocalEvent(request.Patient, ref removed);
        RaiseStateChanged(
            request.Patient,
            request.Module,
            request.ResolutionId,
            NCCyberwareStateChangeKind.Removed,
            previousFunction,
            0f);
        return Applied(1);
    }

    /// <summary>
    /// Applies an EMP to cyberware currently attached to one human. Resistance subtraction, surge damage,
    /// heat and neural disruption are deterministic profile values; no ballistic wound is generated.
    /// </summary>
    public NCCyberwareMutationResult ApplyEmp(in NCCyberwareEmpRequest request)
    {
        if (!request.ResolutionId.IsValid)
            return Reject(NCCyberwareMutationFailure.InvalidResolution);
        if (!float.IsFinite(request.Energy) || request.Energy <= 0f || request.Duration <= TimeSpan.Zero)
            return Reject(NCCyberwareMutationFailure.InvalidAmount);
        if (!TryComp<NCMedicalBodyComponent>(request.Patient, out _))
            return Reject(NCCyberwareMutationFailure.PatientNotAuthoritative);

        var key = new NCCyberwareProcessedActionKey(
            request.ResolutionId.Value,
            NCCyberwareActionKind.Emp,
            request.Sequence,
            0);
        if (IsDuplicate(request.Patient, key))
            return Duplicate();

        var modules = CollectCyberware(request.Patient);
        TryAdvanceBodyPower(request.Patient, _timing.CurTime);
        ApplyBusEmp(request.Patient, request);
        var affected = 0;
        var now = _timing.CurTime;
        foreach (var moduleUid in modules)
        {
            if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
                !TryGetProfile(module.Profile, out var profile))
            {
                continue;
            }

            InitializeState(moduleUid, module, profile);
            var effectiveEnergy = MathF.Max(0f, request.Energy - profile.EmpResistance);
            if (effectiveEnergy <= 0f)
                continue;

            var previousFunction = module.FunctionalEfficiency;
            module.Heat = Math.Clamp(
                module.Heat + MathF.Min(
                    profile.MaximumEmpHeat,
                    effectiveEnergy * profile.EmpHeatPerEnergy),
                0f,
                profile.HeatCapacity);
            module.NeuralIntegrity = Math.Clamp(
                module.NeuralIntegrity - effectiveEnergy * profile.NeuralDamagePerEmpEnergy,
                0f,
                1f);
            if (TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity))
            {
                var surge = MathF.Min(
                    profile.MaximumEmpSurgeDamage,
                    effectiveEnergy * profile.EmpSurgeDamagePerEnergy);
                integrity.CurrentIntegrity = Math.Clamp(
                    integrity.CurrentIntegrity - surge,
                    0f,
                    MathF.Max(0f, integrity.MaxIntegrity));
                DamageAssembly(moduleUid, module, profile, surge);
                NormalizeMechanicalIntegrity(integrity);
            }

            var durationTicks = request.Duration.Ticks * profile.EmpDisableDurationMultiplier;
            var disabledUntil = now + TimeSpan.FromTicks((long) Math.Clamp(durationTicks, 0d, TimeSpan.MaxValue.Ticks));
            if (disabledUntil > module.EmpDisabledUntil)
                module.EmpDisabledUntil = disabledUntil;
            if (profile.NeuralRequirement != NCCyberwareNeuralRequirement.None)
                module.NeuralState = NCCyberwareNeuralState.Disrupted;

            UpdateOperational(moduleUid, module, profile, now);
            Schedule(moduleUid, profile, true);
            RaiseStateChanged(
                request.Patient,
                moduleUid,
                request.ResolutionId,
                NCCyberwareStateChangeKind.Emp,
                previousFunction,
                module.FunctionalEfficiency);
            affected++;
        }

        Remember(request.Patient, key);
        if (affected > 0)
            MarkBodyChanged(request.Patient);
        return Applied(affected);
    }

    public NCCyberwareMutationResult TrySetNeuralInterface(
        EntityUid moduleUid,
        NCActionResolutionId resolutionId,
        bool connected,
        bool calibrated)
    {
        if (!resolutionId.IsValid)
            return Reject(NCCyberwareMutationFailure.InvalidResolution);
        if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryGetProfile(module.Profile, out var profile) ||
            !TryResolveOwningBody(moduleUid, out var body))
        {
            return Reject(NCCyberwareMutationFailure.ModuleUnavailable);
        }
        if (profile.NeuralRequirement == NCCyberwareNeuralRequirement.None)
            return Reject(NCCyberwareMutationFailure.NeuralInterfaceUnavailable);

        InitializeState(moduleUid, module, profile);

        var key = new NCCyberwareProcessedActionKey(
            resolutionId.Value,
            NCCyberwareActionKind.NeuralInterface,
            0,
            0);
        if (IsDuplicate(body, key))
            return Duplicate();

        var previous = module.FunctionalEfficiency;
        TryAdvanceBodyPower(body, _timing.CurTime);
        ApplyRequestedNeuralConnection(module, profile, connected, calibrated);
        UpdateOperational(moduleUid, module, profile, _timing.CurTime);
        Remember(body, key);
        Schedule(moduleUid, profile, true);
        MarkBodyChanged(body);
        RaiseStateChanged(
            body,
            moduleUid,
            resolutionId,
            NCCyberwareStateChangeKind.NeuralInterface,
            previous,
            module.FunctionalEfficiency);
        return Applied(1);
    }

    public NCCyberwareMutationResult TryApplyService(
        EntityUid moduleUid,
        NCActionResolutionId resolutionId,
        ProtoId<NCCyberwareRepairProfilePrototype> repairProfileId,
        float units)
    {
        if (!resolutionId.IsValid)
            return Reject(NCCyberwareMutationFailure.InvalidResolution);
        if (!float.IsFinite(units) || units <= 0f)
            return Reject(NCCyberwareMutationFailure.InvalidAmount);
        if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity) ||
            !TryGetProfile(module.Profile, out var profile) ||
            !TryResolveOwningBody(moduleUid, out var body))
        {
            return Reject(NCCyberwareMutationFailure.ModuleUnavailable);
        }
        if (!SupportsServiceProfile(profile, repairProfileId) ||
            !_prototypes.TryIndex(repairProfileId, out NCCyberwareRepairProfilePrototype? repair))
        {
            return Reject(NCCyberwareMutationFailure.RepairProfileMismatch);
        }

        ValidateRepairProfile(repair);
        if (units > repair.MaximumUnitsPerAction || units > repair.MaximumUnitsPerTarget)
            return Reject(NCCyberwareMutationFailure.InvalidAmount);

        InitializeState(moduleUid, module, profile);
        var key = new NCCyberwareProcessedActionKey(
            resolutionId.Value,
            NCCyberwareActionKind.Service,
            0,
            0);
        if (IsDuplicate(body, key))
            return Duplicate();

        var previous = module.FunctionalEfficiency;
        ApplyServiceUnchecked(moduleUid, module, integrity, profile, repair, units, _timing.CurTime);
        Remember(body, key);
        Schedule(moduleUid, profile, true);
        MarkBodyChanged(body);
        RaiseStateChanged(
            body,
            moduleUid,
            resolutionId,
            NCCyberwareStateChangeKind.MechanicalService,
            previous,
            module.FunctionalEfficiency);
        return Applied(1);
    }

    /// <summary>
    /// Body-targeted reagent/service boundary. Eligible attached modules are ordered by the service profile,
    /// receive only their bounded physical deficit, and share one exactly-once action identifier.
    /// </summary>
    public NCCyberwareMutationResult TryApplyServiceToBody(
        EntityUid body,
        NCActionResolutionId resolutionId,
        ProtoId<NCCyberwareRepairProfilePrototype> repairProfileId,
        float units)
    {
        if (!resolutionId.IsValid)
            return Reject(NCCyberwareMutationFailure.InvalidResolution);
        if (!HasComp<NCMedicalBodyComponent>(body))
            return Reject(NCCyberwareMutationFailure.PatientNotAuthoritative);
        if (!float.IsFinite(units) || units <= 0f ||
            !_prototypes.TryIndex(repairProfileId, out NCCyberwareRepairProfilePrototype? repair))
        {
            return Reject(NCCyberwareMutationFailure.InvalidAmount);
        }

        ValidateRepairProfile(repair);
        if (units > repair.MaximumUnitsPerAction)
            return Reject(NCCyberwareMutationFailure.InvalidAmount);
        var key = new NCCyberwareProcessedActionKey(
            resolutionId.Value,
            NCCyberwareActionKind.Service,
            0,
            0);
        if (IsDuplicate(body, key))
            return Duplicate();

        var candidates = new List<ServiceCandidate>();
        foreach (var moduleUid in CollectCyberware(body))
        {
            if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
                !TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity) ||
                !TryGetProfile(module.Profile, out var profile) ||
                !SupportsServiceProfile(profile, repairProfileId))
            {
                continue;
            }

            InitializeState(moduleUid, module, profile);
            var needed = CalculateServiceNeed(module, integrity, profile, repair);
            if (needed > 0f)
                candidates.Add(new ServiceCandidate(moduleUid, module, integrity, profile, needed));
        }

        candidates.Sort((left, right) => CompareServiceCandidates(left, right, repair.Distribution));
        var remaining = units;
        var affected = 0;
        var now = _timing.CurTime;
        foreach (var candidate in candidates)
        {
            if (remaining <= 0f || affected >= repair.MaximumTargetsPerAction)
                break;
            var appliedUnits = MathF.Min(
                remaining,
                MathF.Min(repair.MaximumUnitsPerTarget, candidate.NeededUnits));
            if (appliedUnits <= 0f)
                continue;

            var previous = candidate.Module.FunctionalEfficiency;
            ApplyServiceUnchecked(
                candidate.Uid,
                candidate.Module,
                candidate.Integrity,
                candidate.Profile,
                repair,
                appliedUnits,
                now);
            Schedule(candidate.Uid, candidate.Profile, true);
            RaiseStateChanged(
                body,
                candidate.Uid,
                resolutionId,
                NCCyberwareStateChangeKind.MechanicalService,
                previous,
                candidate.Module.FunctionalEfficiency);
            remaining -= appliedUnits;
            affected++;
        }

        Remember(body, key);
        if (affected > 0)
            MarkBodyChanged(body);
        return Applied(affected);
    }

    /// <summary>
    /// Combat-facing mechanical trauma boundary. A hit to cyberware reduces frame integrity directly and never
    /// manufactures an organic wound, blood source, infection or pain record.
    /// </summary>
    public NCCyberwareMutationResult TryApplyMechanicalTrauma(
        EntityUid patient,
        EntityUid moduleUid,
        NCActionResolutionId resolutionId,
        uint sourceSequence,
        uint impactSequence,
        float amount)
    {
        if (!resolutionId.IsValid)
            return Reject(NCCyberwareMutationFailure.InvalidResolution);
        if (!float.IsFinite(amount) || amount <= 0f)
            return Reject(NCCyberwareMutationFailure.InvalidAmount);
        if (!TryComp<NCMedicalBodyComponent>(patient, out _))
            return Reject(NCCyberwareMutationFailure.PatientNotAuthoritative);
        if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity) ||
            !TryGetProfile(module.Profile, out var profile) ||
            !TryResolveOwningBody(moduleUid, out var owner) || owner != patient)
        {
            return Reject(NCCyberwareMutationFailure.ModuleUnavailable);
        }

        InitializeState(moduleUid, module, profile);

        var key = new NCCyberwareProcessedActionKey(
            resolutionId.Value,
            NCCyberwareActionKind.MechanicalTrauma,
            sourceSequence,
            impactSequence);
        if (IsDuplicate(patient, key))
            return Duplicate();

        TryAdvanceBodyPower(patient, _timing.CurTime);
        var previous = module.FunctionalEfficiency;
        integrity.CurrentIntegrity = Math.Clamp(
            integrity.CurrentIntegrity - amount,
            0f,
            MathF.Max(0f, integrity.MaxIntegrity));
        DamageAssembly(moduleUid, module, profile, amount);
        NormalizeMechanicalIntegrity(integrity);
        UpdateOperational(moduleUid, module, profile, _timing.CurTime);
        Remember(patient, key);
        Schedule(moduleUid, profile, true);
        MarkBodyChanged(patient);
        RaiseStateChanged(
            patient,
            moduleUid,
            resolutionId,
            NCCyberwareStateChangeKind.MechanicalTrauma,
            previous,
            module.FunctionalEfficiency);
        return Applied(1);
    }

    public bool TrySetLoad(EntityUid moduleUid, float load)
    {
        if (!float.IsFinite(load) ||
            !TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryGetProfile(module.Profile, out var profile) ||
            !TryResolveOwningBody(moduleUid, out _))
        {
            return false;
        }

        if (load > 0f && module.CrisisBlocksActiveModes)
            return false;

        if (TryResolveOwningBody(moduleUid, out var body))
            TryAdvanceBodyPower(body, _timing.CurTime);
        module.Load = Math.Clamp(load, 0f, 1f);
        Schedule(moduleUid, profile, true);
        return true;
    }

    /// <summary>
    /// Advances one attached module. This explicit API is also used by focused deterministic tests and the
    /// persistence capture boundary before it records power and heat.
    /// </summary>
    public bool AdvanceModule(EntityUid moduleUid, TimeSpan elapsed, TimeSpan? observedAt = null)
    {
        if (elapsed <= TimeSpan.Zero ||
            !TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryGetProfile(module.Profile, out var profile) ||
            !TryResolveOwningBody(moduleUid, out var body))
        {
            return false;
        }

        return TryAdvanceBodyPower(body, observedAt ?? _timing.CurTime);
    }

    public List<EntityUid> CollectCyberware(EntityUid body)
    {
        var result = new List<EntityUid>();
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return result;

        foreach (var organ in bodyComponent.Organs.ContainedEntities)
        {
            if (HasComp<NCCyberwareComponent>(organ))
            {
                result.Add(organ);
                if (result.Count > MaximumCyberwarePerBody)
                    return [];
            }

            if (!TryComp<NCCyberwareSlotHostComponent>(organ, out var host) || host.Modules == null)
                continue;
            foreach (var module in host.Modules.ContainedEntities)
            {
                if (result.Count >= MaximumCyberwarePerBody)
                    return [];
                if (HasComp<NCCyberwareComponent>(module))
                    result.Add(module);
            }
        }

        return result;
    }

    /// <summary>
    /// Trusted rejuvenation boundary. It repairs every attached module without changing its physical placement
    /// or semantic manufacturing identity, and clears only runtime replay protection.
    /// </summary>
    public bool ResetBody(EntityUid body)
    {
        if (!HasComp<NCMedicalBodyComponent>(body))
            return false;

        foreach (var moduleUid in CollectCyberware(body))
        {
            if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
                !TryGetProfile(module.Profile, out var profile))
            {
                return false;
            }

            InitializeState(moduleUid, module, profile);
            if (TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity))
            {
                integrity.Material = NCAnatomicalMaterialKind.Mechanical;
                integrity.MaxIntegrity = profile.MaximumIntegrity;
                integrity.CurrentIntegrity = profile.MaximumIntegrity;
                integrity.FunctionalEfficiency = 1f;
                integrity.Necrosis = 0f;
            }

            module.PermanentDamage = 0f;
            if (HasComp<NCCyberwarePowerBusComponent>(moduleUid))
            {
                foreach (var slot in new[] { NCCyberwarePowerBusComponent.MainSlot, NCCyberwarePowerBusComponent.ReserveSlot })
                {
                    if (GetBusCell(moduleUid, slot) is { } cell)
                        _battery.SetCharge(cell.AsNullable(), cell.Comp.MaxCharge);
                }
            }
            if (TryComp<NCCyberwareAssemblyComponent>(moduleUid, out var assembly))
            {
                foreach (var container in assembly.Parts.Values)
                {
                    if (container.ContainedEntity is not { } partUid)
                        continue;
                    var part = Comp<NCCyberwarePartComponent>(partUid);
                    part.PermanentDamage = 0f;
                    var partProfile = _prototypes.Index(part.Profile);
                    var partIntegrity = Comp<NCOrganIntegrityComponent>(partUid);
                    partIntegrity.CurrentIntegrity = partProfile.MaximumIntegrity;
                    NormalizePart(part, partIntegrity, partProfile);
                }
                assembly.ValidatedRevision = null;
                RefreshAssembly(assembly);
            }
            module.CurrentPower = 0f;
            module.ThermalShutdown = false;
            module.Heat = 0f;
            module.Load = 0f;
            module.EmpDisabledUntil = TimeSpan.Zero;
            module.NeuralIntegrity = 1f;
            module.Calibrated = true;
            module.NeuralState = profile.NeuralRequirement == NCCyberwareNeuralRequirement.None
                ? NCCyberwareNeuralState.NotApplicable
                : NCCyberwareNeuralState.Linked;
            module.LastAdvancedAt = _timing.CurTime;
            UpdateOperational(moduleUid, module, profile, _timing.CurTime);
            Schedule(moduleUid, profile, true);
        }

        RemComp<NCCyberwareBodyStateComponent>(body);
        MarkBodyChanged(body);
        return true;
    }

    public bool TryResolveSemanticPlacement(
        EntityUid body,
        EntityUid module,
        out ProtoId<OrganCategoryPrototype>? hostCategory,
        out ProtoId<NCCyberwareSlotPrototype>? slot)
    {
        hostCategory = null;
        slot = null;
        if (!TryComp<NCCyberwareComponent>(module, out var cyberware))
            return false;

        if (TryComp<OrganComponent>(module, out var organ) &&
            organ.Body == body &&
            IsLiveBodyOrgan(body, module))
            return true;
        if (!TryResolveInstalledModule(body, module, out _, out var hostUid, out _, out var installedSlot) ||
            !TryComp<OrganComponent>(hostUid, out var hostOrgan) ||
            hostOrgan.Category is not { } category)
        {
            return false;
        }

        hostCategory = category;
        slot = installedSlot;
        return cyberware.InstalledSlot == installedSlot;
    }

    private void InitializeState(
        EntityUid moduleUid,
        NCCyberwareComponent module,
        NCCyberwarePrototype profile)
    {
        if (module.StableInstanceId == Guid.Empty)
            module.StableInstanceId = Guid.NewGuid();
        InitializeAssembly(moduleUid, module, profile);
        if (module.StateInitialized)
        {
            NormalizeState(module, profile);
            return;
        }

        module.StateInitialized = true;
        module.CurrentPower = 0f;
        module.PowerGranted = !profile.RequiresPower;
        module.Heat = 0f;
        module.Load = 0f;
        module.EmpDisabledUntil = TimeSpan.Zero;
        module.NeuralIntegrity = 1f;
        module.Calibrated = profile.NeuralRequirement == NCCyberwareNeuralRequirement.None;
        module.NeuralState = profile.NeuralRequirement == NCCyberwareNeuralRequirement.None
            ? NCCyberwareNeuralState.NotApplicable
            : NCCyberwareNeuralState.Disconnected;
        module.LastAdvancedAt = _timing.CurTime;
        if (TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity))
        {
            integrity.Material = NCAnatomicalMaterialKind.Mechanical;
            integrity.MaxIntegrity = profile.MaximumIntegrity;
            integrity.CurrentIntegrity = profile.MaximumIntegrity;
            NormalizeMechanicalIntegrity(integrity);
        }

        UpdateOperational(moduleUid, module, profile, _timing.CurTime);
    }

    private void NormalizeState(NCCyberwareComponent module, NCCyberwarePrototype profile)
    {
        module.CurrentPower = 0f;
        module.Heat = Math.Clamp(module.Heat, 0f, profile.HeatCapacity);
        module.Load = Math.Clamp(module.Load, 0f, 1f);
        module.NeuralIntegrity = Math.Clamp(module.NeuralIntegrity, 0f, 1f);
    }

    private void UpdateOperational(
        EntityUid moduleUid,
        NCCyberwareComponent module,
        NCCyberwarePrototype profile,
        TimeSpan now)
    {
        var structural = 1f;
        if (TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity))
        {
            integrity.CurrentIntegrity = MathF.Min(integrity.CurrentIntegrity,
                NCCyberwareCondition.ServiceCeiling(profile.MaximumIntegrity, module.PermanentDamage));
            NormalizeMechanicalIntegrity(integrity);
            structural = SafeFraction(integrity.CurrentIntegrity, profile.MaximumIntegrity);
        }
        if (TryComp<NCCyberwareAssemblyComponent>(moduleUid, out var assembly))
            structural = MathF.Min(structural, assembly.FunctionalEfficiency);

        if (module.CrisisBlocksActiveModes)
            module.Load = 0f;
        var powered = !profile.RequiresPower || module.PowerGranted;
        var heatFactor = module.Heat <= profile.HeatDeratingThreshold
            ? 1f
            : 1f - SafeFraction(
                module.Heat - profile.HeatDeratingThreshold,
                profile.HeatShutdownThreshold - profile.HeatDeratingThreshold);
        heatFactor = Math.Clamp(heatFactor, 0f, 1f);

        var neuralFactor = 1f;
        if (profile.NeuralRequirement != NCCyberwareNeuralRequirement.None)
        {
            if (profile.NeuralRequirement == NCCyberwareNeuralRequirement.Required &&
                module.NeuralState is NCCyberwareNeuralState.Disconnected or NCCyberwareNeuralState.Disrupted)
            {
                neuralFactor = 0f;
            }
            else if (module.NeuralState != NCCyberwareNeuralState.Disconnected)
            {
                neuralFactor = module.NeuralIntegrity;
            }
        }

        var empDisabled = module.EmpDisabledUntil > now;
        module.FunctionalEfficiency = powered && !empDisabled && !module.ThermalShutdown && module.Heat < profile.HeatShutdownThreshold
            ? Math.Clamp(structural * heatFactor * neuralFactor, 0f, 1f)
            : 0f;
        module.Operational = module.FunctionalEfficiency > 0f;

        if (TryComp<NCNerveComponent>(moduleUid, out var nerve) &&
            profile.NeuralRequirement != NCCyberwareNeuralRequirement.None)
        {
            nerve.CyberprostheticInterface = true;
            nerve.NeuralIntegrity = module.NeuralIntegrity;
            nerve.MotorFunction = neuralFactor;
        }
    }

    private static void ApplyRequestedNeuralConnection(
        NCCyberwareComponent module,
        NCCyberwarePrototype profile,
        bool connected,
        bool calibrated)
    {
        if (profile.NeuralRequirement == NCCyberwareNeuralRequirement.None)
        {
            module.Calibrated = true;
            module.NeuralState = NCCyberwareNeuralState.NotApplicable;
            return;
        }

        module.Calibrated = connected && calibrated;
        if (!connected || !module.Calibrated)
            module.NeuralState = NCCyberwareNeuralState.Disconnected;
        else
            module.NeuralState = module.NeuralIntegrity < profile.NeuralDegradedThreshold
                ? NCCyberwareNeuralState.Degraded
                : NCCyberwareNeuralState.Linked;
    }

    private static void UpdateNeuralState(
        NCCyberwareComponent module,
        NCCyberwarePrototype profile,
        TimeSpan now)
    {
        if (profile.NeuralRequirement == NCCyberwareNeuralRequirement.None)
        {
            module.NeuralState = NCCyberwareNeuralState.NotApplicable;
            return;
        }
        if (module.EmpDisabledUntil > now)
        {
            module.NeuralState = NCCyberwareNeuralState.Disrupted;
            return;
        }
        if (!module.Calibrated || module.NeuralIntegrity < profile.NeuralMinimumOperationalIntegrity)
        {
            module.NeuralState = NCCyberwareNeuralState.Disconnected;
            return;
        }

        module.NeuralState = module.NeuralIntegrity < profile.NeuralDegradedThreshold
            ? NCCyberwareNeuralState.Degraded
            : NCCyberwareNeuralState.Linked;
    }

    private void Schedule(EntityUid module, NCCyberwarePrototype profile, bool resetAnchor)
    {
        if (!TryResolveOwningBody(module, out var body))
            return;
        if (resetAnchor && TryComp<NCCyberwareComponent>(module, out var state))
            state.LastAdvancedAt = _timing.CurTime;
        var power = EnsureComp<NCCyberwarePowerStateComponent>(body);
        if (!power.Initialized)
        {
            power.Initialized = true;
            power.LastAdvancedAt = _timing.CurTime;
        }
        RefreshBodyPower(body, _timing.CurTime);
        SchedulePowerBody(body);
    }

    private void SchedulePowerBody(EntityUid body)
    {
        var modules = CollectCyberware(body);
        if (modules.Count == 0)
            return;
        var interval = modules.Min(uid => _prototypes.Index(Comp<NCCyberwareComponent>(uid).Profile).UpdateInterval);
        var due = _timing.CurTime + interval;
        if (_scheduled.TryGetValue(body, out var existing) && existing <= due)
            return;
        _scheduled[body] = due;
        _schedule.Enqueue(body, due.Ticks);
    }

    private void ScheduleContainedModules(EntityUid hostUid, bool active)
    {
        if (!TryComp<NCCyberwareSlotHostComponent>(hostUid, out var host) || host.Modules == null)
            return;
        foreach (var module in host.Modules.ContainedEntities)
        {
            if (!active)
            {
                _scheduled.Remove(module);
                continue;
            }
            if (TryComp<NCCyberwareComponent>(module, out var cyberware) &&
                TryGetProfile(cyberware.Profile, out var profile))
            {
                Schedule(module, profile, true);
            }
        }
    }

    private bool TryResolveHost(
        EntityUid body,
        EntityUid hostUid,
        out NCCyberwareSlotHostComponent host,
        out ProtoId<OrganCategoryPrototype> category)
    {
        host = default!;
        category = default;
        if (!IsLiveBodyOrgan(body, hostUid) ||
            !TryComp<OrganComponent>(hostUid, out var organ) ||
            organ.Body != body || organ.Category is not { } foundCategory ||
            !TryComp(hostUid, out NCCyberwareSlotHostComponent? resolvedHost))
        {
            return false;
        }

        category = foundCategory;
        host = resolvedHost;
        return true;
    }

    private EntityUid? FindInstalledInSlot(
        NCCyberwareSlotHostComponent host,
        ProtoId<NCCyberwareSlotPrototype> slot)
    {
        if (host.Modules == null)
            return null;
        foreach (var module in host.Modules.ContainedEntities)
        {
            if (TryComp<NCCyberwareComponent>(module, out var cyberware) && cyberware.InstalledSlot == slot)
                return module;
        }
        return null;
    }

    private bool TryResolveInstalledModule(
        EntityUid body,
        EntityUid moduleUid,
        out NCCyberwareComponent module,
        out EntityUid hostUid,
        out NCCyberwareSlotHostComponent host,
        out ProtoId<NCCyberwareSlotPrototype> slot)
    {
        module = default!;
        hostUid = default;
        host = default!;
        slot = default;
        if (!TryComp(moduleUid, out NCCyberwareComponent? resolvedModule) ||
            resolvedModule.InstalledSlot is not { } installedSlot ||
            !TryComp<ChildOrganComponent>(moduleUid, out var child) ||
            child.Parent is not { } parent ||
            !TryComp(parent, out NCCyberwareSlotHostComponent? resolvedHost) ||
            resolvedHost.Modules == null ||
            !resolvedHost.Modules.ContainedEntities.Contains(moduleUid) ||
            !TryComp<OrganComponent>(parent, out var parentOrgan) ||
            parentOrgan.Body != body ||
            !IsLiveBodyOrgan(body, parent))
        {
            return false;
        }

        module = resolvedModule;
        host = resolvedHost;
        hostUid = parent;
        slot = installedSlot;
        return true;
    }

    private bool TryResolveOwningBody(EntityUid module, out EntityUid body)
    {
        body = default;
        if (TryComp<OrganComponent>(module, out var organ) &&
            organ.Body is { } directBody &&
            IsLiveBodyOrgan(directBody, module))
        {
            body = directBody;
            return HasComp<NCMedicalBodyComponent>(body);
        }
        if (!TryComp<ChildOrganComponent>(module, out var child) ||
            child.Parent is not { } host ||
            !TryComp<OrganComponent>(host, out var hostOrgan) ||
            hostOrgan.Body is not { } hostBody ||
            !TryComp<NCCyberwareComponent>(module, out var cyberware) ||
            cyberware.InstalledSlot is not { } installedSlot ||
            !TryComp<NCCyberwareSlotHostComponent>(host, out var slotHost) ||
            slotHost.Modules == null ||
            !slotHost.Slots.Contains(installedSlot) ||
            !slotHost.Modules.ContainedEntities.Contains(module) ||
            !IsLiveBodyOrgan(hostBody, host) ||
            !HasComp<NCMedicalBodyComponent>(hostBody))
        {
            return false;
        }

        body = hostBody;
        return true;
    }

    private bool IsLiveBodyOrgan(EntityUid body, EntityUid organ)
    {
        return TryComp<BodyComponent>(body, out var bodyComponent) &&
               bodyComponent.Organs != null &&
               bodyComponent.Organs.ContainedEntities.Contains(organ);
    }

    private void ValidateHostSlots(EntityUid hostUid, NCCyberwareSlotHostComponent host)
    {
        if (!TryComp<OrganComponent>(hostUid, out var organ) || organ.Category is not { } category ||
            host.Slots.Count == 0 || host.Slots.Count != host.Slots.Distinct().Count())
        {
            throw new InvalidOperationException($"NC cyberware slot host {ToPrettyString(hostUid)} is invalid.");
        }

        foreach (var slotId in host.Slots)
        {
            if (!_prototypes.TryIndex(slotId, out NCCyberwareSlotPrototype? slot))
                throw new InvalidOperationException($"NC cyberware slot {slotId} does not exist.");
            ValidateSlot(slot);
            if (!slot.HostCategories.Contains(category))
                throw new InvalidOperationException($"NC cyberware slot {slotId} cannot be hosted by {category}.");
        }
    }

    private void ValidateSlot(NCCyberwareSlotPrototype slot)
    {
        if (!_validatedSlots.Add(slot.ID))
            return;
        if (string.IsNullOrWhiteSpace(slot.Name) || slot.HostCategories.Count == 0 ||
            slot.HostCategories.Count != slot.HostCategories.Distinct().Count() ||
            !float.IsFinite(slot.Capacity) || slot.Capacity <= 0f ||
            slot.HostCategories.Any(category => !_prototypes.HasIndex(category)))
        {
            throw new InvalidOperationException($"NC cyberware slot profile {slot.ID} is invalid.");
        }
    }

    private bool TryGetProfile(
        ProtoId<NCCyberwarePrototype> id,
        out NCCyberwarePrototype profile)
    {
        if (!_prototypes.TryIndex(id, out profile!))
            return false;
        ValidateProfile(profile);
        return true;
    }

    private void ValidateProfile(NCCyberwarePrototype profile)
    {
        if (!_validatedProfiles.Add(profile.ID))
            return;

        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Manufacturer) ||
            !Enum.IsDefined(profile.Form) || !Enum.IsDefined(profile.NeuralRequirement) ||
            !Positive(profile.Size) || !Positive(profile.MaximumIntegrity) ||
            !NonNegative(profile.PowerCapacity) || !Unit(profile.StartingPowerFraction) ||
            !NonNegative(profile.MinimumOperationalPower) ||
            profile.MinimumOperationalPower > profile.PowerCapacity ||
            !NonNegative(profile.IdlePowerPerSecond) || !NonNegative(profile.LoadedPowerPerSecond) ||
            !Positive(profile.HeatCapacity) || !NonNegative(profile.IdleHeatPerSecond) ||
            !NonNegative(profile.LoadedHeatPerSecond) || !NonNegative(profile.PassiveCoolingPerSecond) ||
            !NonNegative(profile.HeatDeratingThreshold) ||
            !Positive(profile.HeatShutdownThreshold) ||
            !Positive(profile.HeatDamageThreshold) ||
            profile.HeatDeratingThreshold >= profile.HeatShutdownThreshold ||
            profile.HeatShutdownThreshold > profile.HeatCapacity ||
            profile.HeatDamageThreshold < profile.HeatShutdownThreshold ||
            profile.HeatDamageThreshold >= profile.HeatCapacity ||
            !NonNegative(profile.HeatDamagePerSecond) || !NonNegative(profile.EmpResistance) ||
            !NonNegative(profile.EmpSurgeDamagePerEnergy) || !NonNegative(profile.MaximumEmpSurgeDamage) ||
            !NonNegative(profile.EmpHeatPerEnergy) || !NonNegative(profile.MaximumEmpHeat) ||
            !Positive(profile.EmpDisableDurationMultiplier) || !NonNegative(profile.NeuralDamagePerEmpEnergy) ||
            !Unit(profile.NeuralDegradedThreshold) || !Unit(profile.NeuralMinimumOperationalIntegrity) ||
            profile.NeuralMinimumOperationalIntegrity > profile.NeuralDegradedThreshold ||
            !Unit(profile.CalibrationMinimumEfficiency) || profile.CalibrationMinimumEfficiency <= 0f ||
            !Positive(profile.CalibrationEnergy) ||
            profile.UpdateInterval <= TimeSpan.Zero ||
            profile.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            profile.Capabilities.Count != profile.Capabilities.Distinct(StringComparer.Ordinal).Count() ||
            !_prototypes.HasIndex(profile.RepairProfile) ||
            profile.AuxiliaryServiceProfiles.Count != profile.AuxiliaryServiceProfiles.Distinct().Count() ||
            profile.AuxiliaryServiceProfiles.Contains(profile.RepairProfile) ||
            profile.AuxiliaryServiceProfiles.Any(service => !_prototypes.HasIndex(service)) ||
            profile.CompatibleSlots.Count != profile.CompatibleSlots.Distinct().Count() ||
            profile.CompatibleSlots.Any(slot => !_prototypes.HasIndex(slot)) ||
            profile.Form == NCCyberwareForm.Implant && profile.CompatibleSlots.Count == 0 ||
            profile.Form != NCCyberwareForm.Implant && profile.CompatibleSlots.Count != 0)
        {
            throw new InvalidOperationException($"NC cyberware profile {profile.ID} is invalid.");
        }

        if (_prototypes.TryIndex(profile.RepairProfile, out NCCyberwareRepairProfilePrototype? repair))
            ValidateRepairProfile(repair);
        foreach (var auxiliary in profile.AuxiliaryServiceProfiles)
        {
            if (_prototypes.TryIndex(auxiliary, out NCCyberwareRepairProfilePrototype? service))
                ValidateRepairProfile(service);
        }
    }

    private void ValidateRepairProfile(NCCyberwareRepairProfilePrototype profile)
    {
        if (!_validatedRepairProfiles.Add(profile.ID))
            return;
        if (string.IsNullOrWhiteSpace(profile.Name) || !Enum.IsDefined(profile.Distribution) ||
            !NonNegative(profile.IntegrityPerUnit) ||
            !NonNegative(profile.PowerPerUnit) || !NonNegative(profile.CoolingPerUnit) ||
            !NonNegative(profile.NeuralIntegrityPerUnit) ||
            (profile.IntegrityPerUnit <= 0f && profile.PowerPerUnit <= 0f &&
             profile.CoolingPerUnit <= 0f && profile.NeuralIntegrityPerUnit <= 0f) ||
            !Positive(profile.MaximumUnitsPerAction) || !Positive(profile.MaximumUnitsPerTarget) ||
            profile.MaximumUnitsPerTarget > profile.MaximumUnitsPerAction ||
            profile.MaximumTargetsPerAction <= 0 || profile.MaximumTargetsPerAction > MaximumCyberwarePerBody)
        {
            throw new InvalidOperationException($"NC cyberware repair profile {profile.ID} is invalid.");
        }
    }

    private bool IsDuplicate(EntityUid body, NCCyberwareProcessedActionKey key)
    {
        return TryComp<NCCyberwareBodyStateComponent>(body, out var state) &&
               state.ProcessedActions.Contains(key);
    }

    private void Remember(EntityUid body, NCCyberwareProcessedActionKey key)
    {
        var state = EnsureComp<NCCyberwareBodyStateComponent>(body);
        if (state.ProcessedActions.Contains(key))
            return;
        var capacity = Math.Max(1, state.ProcessedActionCapacity);
        while (state.ProcessedActions.Count >= capacity)
            state.ProcessedActions.RemoveAt(0);
        state.ProcessedActions.Add(key);
    }

    private void MarkBodyChanged(EntityUid body)
    {
        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Anatomy |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
    }

    private void RaiseStateChanged(
        EntityUid body,
        EntityUid module,
        NCActionResolutionId resolutionId,
        NCCyberwareStateChangeKind kind,
        float previous,
        float current)
    {
        var changed = new NCCyberwareStateChangedEvent(resolutionId, module, kind, previous, current);
        RaiseLocalEvent(body, ref changed);
    }

    private static NCCyberwareMutationResult Reject(NCCyberwareMutationFailure failure)
    {
        return new NCCyberwareMutationResult(NCCyberwareMutationStatus.Rejected, failure);
    }

    private static NCCyberwareMutationResult Applied(int count)
    {
        return new NCCyberwareMutationResult(NCCyberwareMutationStatus.Applied, NCCyberwareMutationFailure.None, count);
    }

    private static NCCyberwareMutationResult Duplicate()
    {
        return new NCCyberwareMutationResult(NCCyberwareMutationStatus.Duplicate, NCCyberwareMutationFailure.None);
    }

    private static float SafeFraction(float value, float maximum)
    {
        if (!float.IsFinite(value) || !float.IsFinite(maximum) || maximum <= 0f)
            return 0f;
        return Math.Clamp(value / maximum, 0f, 1f);
    }

    private static bool Positive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    private static bool NonNegative(float value)
    {
        return float.IsFinite(value) && value >= 0f;
    }

    private static bool Unit(float value)
    {
        return float.IsFinite(value) && value is >= 0f and <= 1f;
    }

    private void ApplyServiceUnchecked(
        EntityUid moduleUid,
        NCCyberwareComponent module,
        NCOrganIntegrityComponent integrity,
        NCCyberwarePrototype profile,
        NCCyberwareRepairProfilePrototype repair,
        float units,
        TimeSpan now)
    {
        integrity.CurrentIntegrity = Math.Clamp(
            integrity.CurrentIntegrity + repair.IntegrityPerUnit * units,
            0f,
            NCCyberwareCondition.ServiceCeiling(profile.MaximumIntegrity, module.PermanentDamage));
        NormalizeMechanicalIntegrity(integrity);
        module.CurrentPower = 0f;
        module.Heat = Math.Clamp(
            module.Heat - repair.CoolingPerUnit * units,
            0f,
            profile.HeatCapacity);
        module.NeuralIntegrity = Math.Clamp(
            module.NeuralIntegrity + repair.NeuralIntegrityPerUnit * units,
            0f,
            1f);
        UpdateNeuralState(module, profile, now);
        UpdateOperational(moduleUid, module, profile, now);
    }

    private static void NormalizeMechanicalIntegrity(NCOrganIntegrityComponent integrity)
    {
        integrity.Material = NCAnatomicalMaterialKind.Mechanical;
        integrity.CurrentIntegrity = Math.Clamp(
            integrity.CurrentIntegrity,
            0f,
            MathF.Max(0f, integrity.MaxIntegrity));
        integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f
            ? integrity.CurrentIntegrity / integrity.MaxIntegrity
            : 0f;
        integrity.Necrosis = 0f;
    }

    private static float CalculateServiceNeed(
        NCCyberwareComponent module,
        NCOrganIntegrityComponent integrity,
        NCCyberwarePrototype profile,
        NCCyberwareRepairProfilePrototype repair)
    {
        var needed = 0f;
        if (repair.IntegrityPerUnit > 0f)
            needed = MathF.Max(needed,
                (NCCyberwareCondition.ServiceCeiling(profile.MaximumIntegrity, module.PermanentDamage) -
                 integrity.CurrentIntegrity) / repair.IntegrityPerUnit);
        if (repair.CoolingPerUnit > 0f)
            needed = MathF.Max(needed, module.Heat / repair.CoolingPerUnit);
        if (repair.NeuralIntegrityPerUnit > 0f)
            needed = MathF.Max(needed, (1f - module.NeuralIntegrity) / repair.NeuralIntegrityPerUnit);
        return Math.Clamp(needed, 0f, repair.MaximumUnitsPerTarget);
    }

    private static int CompareServiceCandidates(
        ServiceCandidate left,
        ServiceCandidate right,
        NCCyberwareServiceDistribution distribution)
    {
        var comparison = distribution switch
        {
            NCCyberwareServiceDistribution.MostDamagedFirst =>
                SafeFraction(left.Integrity.CurrentIntegrity, left.Integrity.MaxIntegrity)
                    .CompareTo(SafeFraction(right.Integrity.CurrentIntegrity, right.Integrity.MaxIntegrity)),
            NCCyberwareServiceDistribution.HottestFirst => right.Module.Heat.CompareTo(left.Module.Heat),
            NCCyberwareServiceDistribution.LowestPowerFirst =>
                SafeFraction(left.Module.CurrentPower, left.Profile.PowerCapacity)
                    .CompareTo(SafeFraction(right.Module.CurrentPower, right.Profile.PowerCapacity)),
            _ => 0,
        };
        return comparison != 0
            ? comparison
            : left.Module.StableInstanceId.CompareTo(right.Module.StableInstanceId);
    }

    private static bool SupportsServiceProfile(
        NCCyberwarePrototype profile,
        ProtoId<NCCyberwareRepairProfilePrototype> repairProfile)
    {
        return profile.RepairProfile == repairProfile || profile.AuxiliaryServiceProfiles.Contains(repairProfile);
    }

    private readonly record struct ServiceCandidate(
        EntityUid Uid,
        NCCyberwareComponent Module,
        NCOrganIntegrityComponent Integrity,
        NCCyberwarePrototype Profile,
        float NeededUnits);
}
