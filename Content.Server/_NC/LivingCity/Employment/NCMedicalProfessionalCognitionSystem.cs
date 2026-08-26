// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Medical.Emergency;
using Content.Server._NC.Medical.Treatment;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Connects the common human planner to ordinary first aid and City Medical clinical work. Every physical result
/// passes through the same inventory, DoAfter, CPR, wound and resource systems used by players.
/// </summary>
public sealed partial class NCMedicalProfessionalCognitionSystem : EntitySystem
{
    private static readonly ProtoId<NCCognitionCapabilityPrototype> GenericResponseCapability =
        "NCMedicalRespondToCritical";
    private static readonly ProtoId<NCCognitionCapabilityPrototype> CityMedicalResponseCapability =
        "NCCityMedicalRespondToCritical";
    private static readonly ProtoId<NCTreatmentCapabilityPrototype> ManualPressureCapability =
        "NCManualPressureCapability";

    private const string ManualPressureAction = "medical-provide-manual-pressure";
    private const string CprAction = "medical-provide-cpr";
    private const string DefinitiveCareAction = "medical-provide-definitive-trauma-care";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCProfessionProfileSystem _professions = default!;
    [Dependency] private NCTreatmentInteractionSystem _treatment = default!;
    [Dependency] private NCEmergencyCareInteractionSystem _emergency = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    private readonly Dictionary<NCActorId, PendingManualPressure> _manualPressure = [];
    private readonly Dictionary<NCActorId, PendingCpr> _cpr = [];
    private readonly Dictionary<NCActorId, TreatmentSession> _treatmentSessions = [];
    private readonly Dictionary<NCActionResolutionId, ResolutionOwner> _resolutionOwners = [];

    private NCCityMedicalWorkPolicyPrototype? _cityMedicalPolicy;
    private long _cprStarted;
    private long _cprCompleted;
    private long _treatmentsStarted;
    private long _treatmentsCompleted;
    private long _clinicalFailures;

    public string? ConfigurationError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCProfessionalWorkRequestedEvent>(OnProfessionalWork);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCTreatmentChangedEvent>(OnTreatmentChanged);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCEmergencyCareChangedEvent>(OnEmergencyCareChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ClearRuntime());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => ClearRuntime());
        RebuildPolicy();
    }

    public NCCityMedicalCognitionCounters GetCounters() => new(
        _cprStarted,
        _cprCompleted,
        _treatmentsStarted,
        _treatmentsCompleted,
        _clinicalFailures,
        ConfigurationError);

    private void OnProfessionalWork(NCProfessionalWorkRequestedEvent args)
    {
        var generic = args.Profile.CapabilityOverlays.Contains(GenericResponseCapability);
        var cityMedical = args.Profile.CapabilityOverlays.Contains(CityMedicalResponseCapability);
        if (!generic && !cityMedical)
            return;

        args.Handled = true;
        if (cityMedical && _cityMedicalPolicy == null)
        {
            args.Failure = "city-medical-policy-unavailable";
            return;
        }
        if (!TryComp<NCCityLocalPerceptionComponent>(args.Worker, out var perception))
        {
            args.Failure = "medical-perception-unavailable";
            return;
        }

        // A casualty keeps routine work incomplete so the higher-priority emergency capability can pre-empt it.
        args.Completed = perception.CriticalCount == 0;
    }

    private void OnExecuteStep(NCHumanActionExecutionEvent args)
    {
        switch (args.Step.ActionId)
        {
            case ManualPressureAction:
                HandleManualPressure(args);
                break;
            case CprAction:
                HandleCpr(args);
                break;
            case DefinitiveCareAction:
                HandleDefinitiveCare(args);
                break;
        }
    }

    private void HandleManualPressure(NCHumanActionExecutionEvent args)
    {
        args.Handled = true;
        if (!TryResolvePatient(args, out var worker, out var profession, out var now, out var patient) ||
            (!profession.CapabilityOverlays.Contains(GenericResponseCapability) &&
             !profession.CapabilityOverlays.Contains(CityMedicalResponseCapability)) ||
            !_prototypes.TryIndex(ManualPressureCapability, out NCTreatmentCapabilityPrototype? capability))
        {
            _manualPressure.Remove(args.ActorId);
            args.Failure = "medical-patient-or-profession-unavailable";
            return;
        }

        if (_manualPressure.TryGetValue(args.ActorId, out var pending))
        {
            if (pending.Patient != patient || !Exists(patient))
            {
                _manualPressure.Remove(args.ActorId);
                args.Failure = "medical-treatment-target-changed";
                return;
            }
            if (now < pending.ResolveAt)
                return;

            _manualPressure.Remove(args.ActorId);
            if (TryComp<Content.Shared._NC.Medical.Wounds.NCWoundContainerComponent>(patient, out var wounds) &&
                wounds.Wounds.TryGetValue(pending.WoundId, out var wound) &&
                wound.TreatmentFlags.HasFlag(Content.Shared._NC.Medical.Wounds.NCWoundTreatmentFlags.Pressure))
            {
                args.Completed = true;
                return;
            }

            args.Failure = "medical-manual-pressure-interrupted";
            return;
        }

        if (!_treatment.TrySelectWound(worker, patient, NCTreatmentKind.Pressure, out var woundId))
        {
            args.Completed = true;
            return;
        }
        if (!_treatment.TryStartManualPressure(worker, patient))
        {
            args.Failure = "medical-manual-pressure-rejected";
            return;
        }

        _manualPressure[args.ActorId] = new PendingManualPressure(
            patient,
            woundId,
            now.Advance(capability.ApplicationDuration + TimeSpan.FromSeconds(1)));
    }

    private void HandleCpr(NCHumanActionExecutionEvent args)
    {
        args.Handled = true;
        if (!TryResolveCityMedicalPatient(args, out var worker, out var now, out var patient, out var policy))
        {
            ClearCpr(args.ActorId);
            args.Failure = "city-medical-patient-or-profession-unavailable";
            return;
        }
        if (!TryComp<NCPhysiologyComponent>(patient, out var physiology))
        {
            args.Failure = "city-medical-physiology-unavailable";
            return;
        }

        if (_cpr.TryGetValue(args.ActorId, out var pending))
        {
            if (pending.Patient != patient || pending.IntentionId != args.IntentionId)
            {
                ClearCpr(args.ActorId);
                args.Failure = "city-medical-cpr-target-changed";
                return;
            }
            if (pending.Observed)
            {
                ClearCpr(args.ActorId);
                if (pending.Outcome is NCEmergencyCareOutcome.Applied or NCEmergencyCareOutcome.NotIndicated)
                {
                    _cprCompleted++;
                    args.Completed = true;
                }
                else
                {
                    _clinicalFailures++;
                    args.Failure = $"city-medical-cpr-failed:{pending.Outcome}";
                }
                return;
            }
            if (now >= pending.TimeoutAt)
            {
                ClearCpr(args.ActorId);
                _clinicalFailures++;
                args.Failure = "city-medical-cpr-timeout";
            }
            return;
        }

        // CPR is a conditional procedure step. A critical patient who has not arrested proceeds to wound care.
        if (physiology.Condition != NCPhysiologicalCondition.ClinicalArrest)
        {
            args.Completed = true;
            return;
        }
        if (!_emergency.TryStartCpr(worker, patient, out var resolutionId, out var duration))
        {
            _clinicalFailures++;
            args.Failure = "city-medical-cpr-rejected";
            return;
        }

        _cprStarted++;
        _cpr[args.ActorId] = new PendingCpr(
            args.IntentionId,
            patient,
            resolutionId,
            now.Advance(duration + policy.CompletionGrace));
        _resolutionOwners[resolutionId] = new ResolutionOwner(args.ActorId, CprAction);
    }

    private void HandleDefinitiveCare(NCHumanActionExecutionEvent args)
    {
        args.Handled = true;
        if (!TryResolveCityMedicalPatient(args, out var worker, out var now, out var patient, out var policy))
        {
            ClearTreatmentSession(args.ActorId);
            args.Failure = "city-medical-patient-or-profession-unavailable";
            return;
        }

        if (!_treatmentSessions.TryGetValue(args.ActorId, out var session))
        {
            session = new TreatmentSession(args.IntentionId, patient);
            _treatmentSessions.Add(args.ActorId, session);
        }
        else if (session.IntentionId != args.IntentionId || session.Patient != patient)
        {
            ClearTreatmentSession(args.ActorId);
            args.Failure = "city-medical-treatment-target-changed";
            return;
        }

        if (session.Pending != null)
        {
            var pending = session.Pending;
            if (pending.Observed)
            {
                _resolutionOwners.Remove(pending.ResolutionId);
                session.Pending = null;
                session.CompletedInterventions++;
                _treatmentsCompleted++;
            }
            else if (now >= pending.TimeoutAt)
            {
                ClearTreatmentSession(args.ActorId);
                _clinicalFailures++;
                args.Failure = "city-medical-treatment-timeout";
                return;
            }
            else
            {
                return;
            }
        }

        if (session.CompletedInterventions >= policy.MaximumInterventionsPerPatient)
        {
            ClearTreatmentSession(args.ActorId);
            args.Completed = true;
            return;
        }

        if (!TryFindTreatmentItem(worker, patient, policy, out var item))
        {
            var treatmentStillIndicated = policy.TreatmentPriority.Any(kind =>
                _treatment.TrySelectWound(worker, patient, kind, out _));
            ClearTreatmentSession(args.ActorId);
            if (treatmentStillIndicated)
            {
                _clinicalFailures++;
                args.Failure = "city-medical-supplies-unavailable";
            }
            else
            {
                args.Completed = true;
            }
            return;
        }

        if (!_hands.IsHolding(worker, item) && !_hands.TryPickupAnyHand(worker, item))
        {
            ClearTreatmentSession(args.ActorId);
            _clinicalFailures++;
            args.Failure = "city-medical-cannot-ready-supplies";
            return;
        }
        if (!_treatment.TryStartItemTreatment(
                worker,
                item,
                patient,
                out var resolutionId,
                out _,
                out _,
                out var duration))
        {
            ClearTreatmentSession(args.ActorId);
            _clinicalFailures++;
            args.Failure = "city-medical-treatment-rejected";
            return;
        }

        _treatmentsStarted++;
        session.Pending = new PendingTreatment(resolutionId, now.Advance(duration + policy.CompletionGrace));
        _resolutionOwners[resolutionId] = new ResolutionOwner(args.ActorId, DefinitiveCareAction);
    }

    private bool TryFindTreatmentItem(
        EntityUid worker,
        EntityUid patient,
        NCCityMedicalWorkPolicyPrototype policy,
        out EntityUid item)
    {
        item = default;
        var candidates = new List<EntityUid>(policy.MaximumInventoryCandidates);
        var seen = new HashSet<EntityUid>();
        foreach (var root in _inventory.GetHandOrInventoryEntities(worker))
        {
            AddCandidate(root);
            if (candidates.Count >= policy.MaximumInventoryCandidates)
                break;
            if (!TryComp<StorageComponent>(root, out var storage))
                continue;

            // Only direct worn storage is inspected. Nested-container recursion is intentionally excluded.
            foreach (var contained in storage.Container.ContainedEntities)
            {
                AddCandidate(contained);
                if (candidates.Count >= policy.MaximumInventoryCandidates)
                    break;
            }
            if (candidates.Count >= policy.MaximumInventoryCandidates)
                break;
        }

        foreach (var priority in policy.TreatmentPriority)
        {
            foreach (var candidate in candidates)
            {
                if (_treatment.CanUseItemTreatment(worker, candidate, patient, out var kind, out _) &&
                    kind == priority)
                {
                    item = candidate;
                    return true;
                }
            }
        }
        return false;

        void AddCandidate(EntityUid candidate)
        {
            if (candidates.Count < policy.MaximumInventoryCandidates && seen.Add(candidate) &&
                HasComp<NCTreatmentItemComponent>(candidate))
            {
                candidates.Add(candidate);
            }
        }
    }

    private bool TryResolvePatient(
        NCHumanActionExecutionEvent args,
        out EntityUid worker,
        out NCProfessionProfilePrototype profession,
        out NCActiveWorldTime now,
        out EntityUid patient)
    {
        worker = default;
        profession = default!;
        now = default;
        patient = default;
        if (args.EntityUid is not { } resolvedWorker || !Exists(resolvedWorker) ||
            !_professions.TryGetProfile(args.ActorId, out profession) ||
            !_clock.TryGetCurrentTime(out _, out now) ||
            args.Intention.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !_perception.TryResolveSubjectHandle(args.ActorId, args.Intention.Target.StableId, now, out patient) ||
            patient == resolvedWorker)
        {
            return false;
        }

        worker = resolvedWorker;
        return true;
    }

    private bool TryResolveCityMedicalPatient(
        NCHumanActionExecutionEvent args,
        out EntityUid worker,
        out NCActiveWorldTime now,
        out EntityUid patient,
        out NCCityMedicalWorkPolicyPrototype policy)
    {
        policy = _cityMedicalPolicy!;
        if (policy == null || !TryResolvePatient(args, out worker, out var profession, out now, out patient) ||
            profession.Organization != policy.Organization ||
            !profession.CapabilityOverlays.Contains(policy.Capability))
        {
            worker = default;
            now = default;
            patient = default;
            return false;
        }
        return true;
    }

    private void OnTreatmentChanged(Entity<NCMedicalBodyComponent> patient, ref NCTreatmentChangedEvent args)
    {
        if (!_resolutionOwners.TryGetValue(args.ResolutionId, out var owner) ||
            owner.ActionId != DefinitiveCareAction ||
            !_treatmentSessions.TryGetValue(owner.ActorId, out var session) ||
            session.Patient != patient.Owner || session.Pending?.ResolutionId != args.ResolutionId)
        {
            return;
        }

        session.Pending.Observed = true;
    }

    private void OnEmergencyCareChanged(Entity<NCMedicalBodyComponent> patient, ref NCEmergencyCareChangedEvent args)
    {
        if (!_resolutionOwners.TryGetValue(args.ResolutionId, out var owner) ||
            owner.ActionId != CprAction ||
            !_cpr.TryGetValue(owner.ActorId, out var pending) ||
            pending.Patient != patient.Owner || pending.ResolutionId != args.ResolutionId)
        {
            return;
        }

        pending.Observed = true;
        pending.Outcome = args.Outcome;
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        switch (args.ActionId)
        {
            case ManualPressureAction:
                _manualPressure.Remove(args.ActorId);
                break;
            case CprAction:
                ClearCpr(args.ActorId);
                break;
            case DefinitiveCareAction:
                ClearTreatmentSession(args.ActorId);
                break;
        }
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCCityMedicalWorkPolicyPrototype>() ||
            args.WasModified<NCCognitionCapabilityPrototype>() ||
            args.WasModified<NCTreatmentCapabilityPrototype>())
        {
            RebuildPolicy();
        }
    }

    private void RebuildPolicy()
    {
        ConfigurationError = null;
        _cityMedicalPolicy = null;
        var policies = _prototypes.EnumeratePrototypes<NCCityMedicalWorkPolicyPrototype>()
            .Where(candidate => candidate.Capability == CityMedicalResponseCapability)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToArray();
        if (policies.Length != 1)
        {
            RejectPolicy($"Exactly one City Medical policy for {CityMedicalResponseCapability} must be defined.");
            return;
        }

        var policy = policies[0];
        if (!_prototypes.HasIndex(policy.Organization) || !_prototypes.HasIndex(policy.Capability) ||
            policy.TreatmentPriority.Length == 0 ||
            policy.TreatmentPriority.Distinct().Count() != policy.TreatmentPriority.Length ||
            policy.TreatmentPriority.Any(kind => kind is not (NCTreatmentKind.Bandage or
                NCTreatmentKind.Hemostatic or NCTreatmentKind.Tourniquet)) ||
            policy.MaximumInventoryCandidates <= 0 || policy.MaximumInterventionsPerPatient <= 0 ||
            policy.CompletionGrace < TimeSpan.Zero)
        {
            RejectPolicy($"City Medical policy {policy.ID} contains invalid clinical limits or references.");
            return;
        }

        _cityMedicalPolicy = policy;
    }

    private void RejectPolicy(string error)
    {
        ConfigurationError = error;
        Log.Error(error);
    }

    private void ClearCpr(NCActorId actorId)
    {
        if (_cpr.Remove(actorId, out var pending))
            _resolutionOwners.Remove(pending.ResolutionId);
    }

    private void ClearTreatmentSession(NCActorId actorId)
    {
        if (_treatmentSessions.Remove(actorId, out var session) && session.Pending != null)
            _resolutionOwners.Remove(session.Pending.ResolutionId);
    }

    private void ClearRuntime()
    {
        _manualPressure.Clear();
        _cpr.Clear();
        _treatmentSessions.Clear();
        _resolutionOwners.Clear();
    }

    private readonly record struct PendingManualPressure(
        EntityUid Patient,
        Guid WoundId,
        NCActiveWorldTime ResolveAt);

    private sealed class PendingCpr(
        Guid intentionId,
        EntityUid patient,
        NCActionResolutionId resolutionId,
        NCActiveWorldTime timeoutAt)
    {
        public Guid IntentionId { get; } = intentionId;
        public EntityUid Patient { get; } = patient;
        public NCActionResolutionId ResolutionId { get; } = resolutionId;
        public NCActiveWorldTime TimeoutAt { get; } = timeoutAt;
        public bool Observed;
        public NCEmergencyCareOutcome Outcome;
    }

    private sealed class PendingTreatment(NCActionResolutionId resolutionId, NCActiveWorldTime timeoutAt)
    {
        public NCActionResolutionId ResolutionId { get; } = resolutionId;
        public NCActiveWorldTime TimeoutAt { get; } = timeoutAt;
        public bool Observed;
    }

    private sealed class TreatmentSession(Guid intentionId, EntityUid patient)
    {
        public Guid IntentionId { get; } = intentionId;
        public EntityUid Patient { get; } = patient;
        public int CompletedInterventions;
        public PendingTreatment? Pending;
    }

    private readonly record struct ResolutionOwner(NCActorId ActorId, string ActionId);
}

public readonly record struct NCCityMedicalCognitionCounters(
    long CprStarted,
    long CprCompleted,
    long TreatmentsStarted,
    long TreatmentsCompleted,
    long ClinicalFailures,
    string? ConfigurationError);
