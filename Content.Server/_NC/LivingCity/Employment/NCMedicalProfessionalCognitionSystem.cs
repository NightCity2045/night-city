// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Medical.Treatment;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Connects the common human planner to ordinary deterministic first aid. Medical NPCs perceive and approach the
/// same patient as any human would, then use the same range, DoAfter, wound and treatment systems as a player.
/// </summary>
public sealed partial class NCMedicalProfessionalCognitionSystem : EntitySystem
{
    private static readonly ProtoId<NCCognitionCapabilityPrototype> ResponseCapability =
        "NCMedicalRespondToCritical";
    private static readonly ProtoId<NCTreatmentCapabilityPrototype> ManualPressureCapability =
        "NCManualPressureCapability";
    private const string TreatmentAction = "medical-provide-manual-pressure";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCProfessionProfileSystem _professions = default!;
    [Dependency] private NCTreatmentInteractionSystem _treatment = default!;

    private readonly Dictionary<Content.Shared._NC.Persistence.Identity.NCActorId, PendingTreatment> _pending = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCognitionExecuteStepEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCProfessionalWorkRequestedEvent>(OnProfessionalWork);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _pending.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _pending.Clear());
    }

    private void OnProfessionalWork(NCProfessionalWorkRequestedEvent args)
    {
        if (!args.Profile.CapabilityOverlays.Contains(ResponseCapability))
            return;
        args.Handled = true;
        if (!TryComp<NCCityLocalPerceptionComponent>(args.Worker, out var perception))
        {
            args.Failure = "medical-perception-unavailable";
            return;
        }

        // The bounded local triage scan is real professional work. A detected casualty is deliberately left
        // incomplete so the higher-priority emergency capability can pre-empt this routine on the next decision.
        args.Completed = perception.CriticalCount == 0;
    }

    private void OnExecuteStep(NCCognitionExecuteStepEvent args)
    {
        if (args.Step.ActionId != TreatmentAction)
            return;

        args.Handled = true;
        if (args.EntityUid is not { } worker || !Exists(worker) ||
            !_professions.TryGetProfile(args.ActorId, out var profession) ||
            !profession.CapabilityOverlays.Contains(ResponseCapability) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            args.Intention.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !_perception.TryResolveSubjectHandle(args.ActorId, args.Intention.Target.StableId, now, out var patient) ||
            patient == worker ||
            !_prototypes.TryIndex(ManualPressureCapability, out NCTreatmentCapabilityPrototype? capability))
        {
            _pending.Remove(args.ActorId);
            args.Failure = "medical-patient-or-profession-unavailable";
            return;
        }

        if (_pending.TryGetValue(args.ActorId, out var pending))
        {
            if (pending.Patient != patient || !Exists(patient))
            {
                _pending.Remove(args.ActorId);
                args.Failure = "medical-treatment-target-changed";
                return;
            }
            if (now < pending.ResolveAt)
                return;

            _pending.Remove(args.ActorId);
            if (TryComp<NCWoundContainerComponent>(patient, out var wounds) &&
                wounds.Wounds.TryGetValue(pending.WoundId, out var wound) &&
                wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Pressure))
            {
                args.Completed = true;
                return;
            }

            args.Failure = "medical-manual-pressure-interrupted";
            return;
        }

        if (!_treatment.TrySelectWound(worker, patient, NCTreatmentKind.Pressure, out var woundId))
        {
            // No externally bleeding wound remains. Another medical authority may already have stabilized them.
            args.Completed = true;
            return;
        }
        if (!_treatment.TryStartManualPressure(worker, patient))
        {
            args.Failure = "medical-manual-pressure-rejected";
            return;
        }

        _pending[args.ActorId] = new PendingTreatment(
            patient,
            woundId,
            now.Advance(capability.ApplicationDuration + TimeSpan.FromSeconds(1)));
    }

    private readonly record struct PendingTreatment(
        EntityUid Patient,
        Guid WoundId,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime ResolveAt);
}
