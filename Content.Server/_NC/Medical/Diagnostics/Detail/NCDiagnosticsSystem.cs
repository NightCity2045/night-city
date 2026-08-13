// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server.Medical.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Players.RateLimiting;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.CCVar;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Players.RateLimiting;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Diagnostics.Detail;

/// <summary>
/// Builds capability-filtered medical snapshots from one body's bounded organ/wound graph. There are no
/// diagnostic rolls, false negatives, global scans, Update loops or broadcast medical state.
/// </summary>
public sealed partial class NCDiagnosticsSystem : EntitySystem
{
    private const string RequestRateLimitKey = "NCMedicalDiagnostics";
    private const int AbsoluteMaxRegions = 32;
    private const int AbsoluteMaxWounds = 64;
    private const int AbsoluteMaxOrgans = 64;
    private const int AbsoluteMaxCyberware = 64;
    private const int AbsoluteMaxDrugs = 32;
    private const int AbsoluteMaxHistory = 128;
    private const int OrganTraversalLimit = 64;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private SharedBatterySystem _battery = default!;
    [Dependency] private PlayerRateLimitManager _rateLimit = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;

    private readonly Dictionary<NCDiagnosticTier, ProtoId<NCDiagnosticCapabilityPrototype>> _unassisted = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<NCDiagnosticSnapshotRequest>(OnSnapshotRequest);
        SubscribeLocalEvent<NCDiagnosticCapabilityComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCDiagnosticCapabilityComponent, NCHealthAnalyzerScanCompletedEvent>(OnAnalyzerScanCompleted);
        SubscribeLocalEvent<NCDiagnosticCapabilityComponent, GetVerbsEvent<AlternativeVerb>>(OnGetScannerVerbs);
        SubscribeLocalEvent<NCMedicalBodyComponent, GetVerbsEvent<AlternativeVerb>>(OnGetPatientDiagnosticVerbs);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        _rateLimit.Register(
            RequestRateLimitKey,
            new RateLimitRegistration(
                CCVars.NCMedicalDiagnosticsRateLimitPeriod,
                CCVars.NCMedicalDiagnosticsRateLimitCount,
                null));
        CacheUnassistedCapabilities();
    }

    /// <summary>
    /// Low-level deterministic builder used after access validation and by server tests/debug tooling.
    /// </summary>
    public bool TryBuildSnapshot(
        EntityUid target,
        ProtoId<NCDiagnosticCapabilityPrototype> capability,
        out NCMedicalSnapshot snapshot)
    {
        return TryBuildSnapshot(target, capability, NCActionResolutionId.New(), _timing.CurTime, out snapshot);
    }

    /// <summary>
    /// Explicit observation time makes replay and deterministic-repeat tests independent of wall-clock progress.
    /// </summary>
    public bool TryBuildSnapshot(
        EntityUid target,
        ProtoId<NCDiagnosticCapabilityPrototype> capability,
        NCActionResolutionId resolutionId,
        TimeSpan observedAt,
        out NCMedicalSnapshot snapshot)
    {
        snapshot = default!;
        if (!resolutionId.IsValid ||
            !TryComp<NCMedicalBodyComponent>(target, out var medical) ||
            !_anatomy.TryGetProfile((target, medical), out var anatomyProfile) ||
            !_prototypes.TryIndex(capability, out NCDiagnosticCapabilityPrototype? policy) ||
            policy.Tier == NCDiagnosticTier.AdminExact)
        {
            return false;
        }

        var organs = CollectOrgans(target);
        var wounds = CollectWounds(target);
        var bleeding = CompOrNull<NCBleedingComponent>(target);
        var physiology = CompOrNull<NCPhysiologyComponent>(target);
        var circulation = CompOrNull<NCCirculationComponent>(target);
        var warnings = new HashSet<NCDiagnosticWarning>();

        var vitals = BuildVitals(target, policy, physiology, circulation, bleeding, warnings);
        var regions = BuildRegions(
            target,
            medical,
            anatomyProfile,
            policy,
            organs,
            wounds,
            bleeding,
            warnings);
        var woundRows = BuildWounds(policy, wounds, out var woundsOmitted, warnings);
        var organRows = BuildOrgans(policy, organs, out var organsOmitted, warnings);
        var cyberwareRows = BuildCyberware(
            target,
            policy,
            observedAt,
            out var cyberwareOmitted,
            warnings);
        var drugRows = BuildDrugs(target, policy, out var drugsOmitted, warnings);
        var historyRows = BuildHistory(target, policy, out var historyOmitted);
        if (Has(policy.Disclosures, NCDiagnosticDisclosure.Infection) &&
            _systemic.GetBurden(target, NCSystemicChannel.Sepsis) > 0f)
        {
            warnings.Add(NCDiagnosticWarning.Sepsis);
        }
        var surgery = BuildSurgery(policy, organs, warnings);

        snapshot = new NCMedicalSnapshot(
            resolutionId,
            policy.Tier,
            policy.Disclosures,
            medical.SnapshotVersion,
            medical.MedicalRevision,
            observedAt,
            vitals,
            regions,
            woundRows,
            organRows,
            cyberwareRows,
            drugRows,
            historyRows,
            surgery,
            warnings.OrderBy(value => value).ToList(),
            (ushort) Math.Clamp(woundsOmitted, 0, ushort.MaxValue),
            (ushort) Math.Clamp(organsOmitted, 0, ushort.MaxValue),
            (ushort) Math.Clamp(cyberwareOmitted, 0, ushort.MaxValue),
            (ushort) Math.Clamp(drugsOmitted, 0, ushort.MaxValue),
            (ushort) Math.Clamp(historyOmitted, 0, ushort.MaxValue));
        return true;
    }

    private List<NCMedicalHistorySummary> BuildHistory(
        EntityUid body,
        NCDiagnosticCapabilityPrototype policy,
        out int omitted)
    {
        omitted = 0;
        if (!Has(policy.Disclosures, NCDiagnosticDisclosure.TreatmentHistory) ||
            !TryComp<NCMedicalHistoryComponent>(body, out var history))
        {
            return [];
        }

        var limit = Math.Clamp(policy.MaxHistoryRecords, 0, AbsoluteMaxHistory);
        var ordered = history.Records
            .OrderByDescending(record => record.Timestamp)
            .ToList();
        omitted = Math.Max(0, ordered.Count - limit);
        return ordered.Take(limit)
            .Select(record => new NCMedicalHistorySummary(
                record.Timestamp,
                record.Kind,
                record.Zone,
                record.WoundSeverity,
                record.WoundType,
                record.Treatment,
                record.EmergencyCare,
                record.Procedure,
                record.SurgeryStep,
                record.SurgeryOutcome,
                record.Drug,
                record.AutopsyRegion,
                record.AutopsyWoundCount,
                record.AutopsyWoundTypes is { } types ? new List<NCWoundType>(types) : null))
            .ToList();
    }

    /// <summary>
    /// Validates actor, target, physical capability, held/powered state and unobstructed range before building.
    /// </summary>
    public bool TryBuildForActor(
        EntityUid actor,
        EntityUid target,
        EntityUid? tool,
        NCDiagnosticTier requestedTier,
        out NCMedicalSnapshot snapshot,
        out NCDiagnosticAccessFailure failure)
    {
        return TryBuildForActor(
            NCActionResolutionId.New(),
            actor,
            target,
            tool,
            requestedTier,
            out snapshot,
            out failure);
    }

    /// <summary>
    /// Trusted server API for carrying an existing action correlation into the diagnostic result.
    /// </summary>
    public bool TryBuildForActor(
        NCActionResolutionId resolutionId,
        EntityUid actor,
        EntityUid target,
        EntityUid? tool,
        NCDiagnosticTier requestedTier,
        out NCMedicalSnapshot snapshot,
        out NCDiagnosticAccessFailure failure)
    {
        snapshot = default!;
        failure = NCDiagnosticAccessFailure.None;
        if (!resolutionId.IsValid)
        {
            failure = NCDiagnosticAccessFailure.InvalidResolution;
            return false;
        }

        if (!Exists(actor))
        {
            failure = NCDiagnosticAccessFailure.ActorUnavailable;
            return false;
        }

        if (!Exists(target))
        {
            failure = NCDiagnosticAccessFailure.TargetUnavailable;
            return false;
        }

        if (!HasComp<NCMedicalBodyComponent>(target))
        {
            failure = NCDiagnosticAccessFailure.TargetNotMedicalBody;
            return false;
        }

        if (requestedTier == NCDiagnosticTier.AdminExact)
        {
            failure = NCDiagnosticAccessFailure.AdminServerOnly;
            return false;
        }

        if (!TryResolveCapability(tool, requestedTier, out var capabilityId, out var capability, out failure))
            return false;

        if (capability.SelfOnly && actor != target)
        {
            failure = NCDiagnosticAccessFailure.SelfOnly;
            return false;
        }

        if (tool is { } toolUid)
        {
            if (!TryComp<NCDiagnosticCapabilityComponent>(toolUid, out var observed) || !observed.Operational)
            {
                failure = NCDiagnosticAccessFailure.ToolNotOperational;
                return false;
            }

            if (capability.RequiresHeld && !_hands.IsHolding(actor, toolUid))
            {
                failure = NCDiagnosticAccessFailure.ToolNotHeld;
                return false;
            }

            if (capability.RequiresPower &&
                !_power.IsPowered(toolUid) &&
                (!TryComp<BatteryComponent>(toolUid, out var battery) ||
                 _battery.GetCharge((toolUid, battery)) <= 0f))
            {
                failure = NCDiagnosticAccessFailure.ToolUnpowered;
                return false;
            }

            var inRange = capability.RequiresHeld
                ? _interaction.InRangeUnobstructed(actor, target, capability.Range)
                : _interaction.InRangeUnobstructed(actor, toolUid, capability.Range) &&
                  _interaction.InRangeUnobstructed(toolUid, target, capability.Range);
            if (!inRange)
            {
                failure = NCDiagnosticAccessFailure.OutOfRange;
                return false;
            }
        }
        else if (actor != target && !_interaction.InRangeUnobstructed(actor, target, capability.Range))
        {
            failure = NCDiagnosticAccessFailure.OutOfRange;
            return false;
        }

        if (!TryBuildSnapshot(target, capabilityId, resolutionId, _timing.CurTime, out snapshot))
        {
            failure = NCDiagnosticAccessFailure.TierUnavailable;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Trusted server-only exact API. No network message can select this tier.
    /// </summary>
    public bool TryBuildAdminExactSnapshot(EntityUid target, out NCAdminExactMedicalSnapshot snapshot)
    {
        return TryBuildAdminExactSnapshot(target, NCActionResolutionId.New(), out snapshot);
    }

    /// <summary>
    /// Trusted server-only exact API with an explicit action correlation.
    /// </summary>
    public bool TryBuildAdminExactSnapshot(
        EntityUid target,
        NCActionResolutionId resolutionId,
        out NCAdminExactMedicalSnapshot snapshot)
    {
        snapshot = default!;
        if (!resolutionId.IsValid || !TryComp<NCMedicalBodyComponent>(target, out var medical))
            return false;

        var organs = CollectOrgans(target);
        var wounds = CollectWounds(target);
        var physiology = CompOrNull<NCPhysiologyComponent>(target);
        var circulation = CompOrNull<NCCirculationComponent>(target);
        var bleeding = CompOrNull<NCBleedingComponent>(target);

        var exactWounds = wounds.Select(wound => new NCAdminExactWoundSummary(
                wound.WoundId,
                wound.Prototype,
                wound.TargetZone,
                wound.RegionCategory,
                wound.PrimaryDamageType,
                wound.WoundType,
                wound.TissueDepth,
                wound.Severity,
                wound.Stage,
                wound.ClosureState,
                wound.IntegrityDamage.Float(),
                wound.ExternalBleedRate,
                wound.InternalBleedRate,
                wound.Pain,
                wound.Contamination,
                wound.Infection,
                wound.InfectionStage,
                wound.Necrosis,
                wound.HealingProgress,
                wound.EmbeddedObjectId,
                wound.ForensicResolutionId,
                wound.CreatedAt,
                wound.ModifiedAt))
            .ToList();

        var exactScars = TryComp<NCWoundContainerComponent>(target, out var woundContainer)
            ? woundContainer.Scars.Select(scar => new NCAdminExactScarSummary(
                    scar.ScarId,
                    scar.SourceWoundId,
                    scar.TargetZone,
                    scar.RegionCategory,
                    scar.WoundType,
                    scar.OriginalSeverity,
                    scar.Area,
                    scar.Disfigurement,
                    scar.Contracture,
                    scar.CreatedAt))
                .ToList()
            : [];

        var exactOrgans = new List<NCAdminExactOrganSummary>(organs.Count);
        foreach (var organ in organs)
        {
            TryComp<NCOrganIntegrityComponent>(organ.Uid, out var integrity);
            TryComp<NCOrganFunctionComponent>(organ.Uid, out var function);
            TryComp<NCBoneComponent>(organ.Uid, out var bone);
            TryComp<NCSurgicalSiteComponent>(organ.Uid, out var site);
            TryComp<NCTransplantRejectionComponent>(organ.Uid, out var rejection);
            exactOrgans.Add(new NCAdminExactOrganSummary(
                organ.Category,
                integrity?.CurrentIntegrity ?? 0f,
                integrity?.MaxIntegrity ?? 0f,
                integrity?.FunctionalEfficiency ?? 0f,
                function?.FunctionType,
                function?.CurrentCapacity,
                function?.Vital ?? false,
                bone?.State,
                bone?.Stability,
                site?.AccessDepth,
                rejection?.Burden ?? 0f,
                rejection?.Stage));
        }

        var exactCyberware = new List<NCAdminExactCyberwareSummary>();
        foreach (var moduleUid in _cyberware.CollectCyberware(target))
        {
            if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
                !_cyberware.TryResolveSemanticPlacement(target, moduleUid, out var hostCategory, out var slot))
            {
                continue;
            }

            TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity);
            exactCyberware.Add(new NCAdminExactCyberwareSummary(
                module.StableInstanceId,
                module.Profile,
                hostCategory,
                slot,
                integrity?.CurrentIntegrity ?? 0f,
                integrity?.MaxIntegrity ?? 0f,
                module.CurrentPower,
                module.Heat,
                module.Load,
                module.EmpDisabledUntil,
                module.NeuralIntegrity,
                module.NeuralState,
                module.Calibrated,
                module.Operational,
                module.FunctionalEfficiency));
        }

        var exactDrugs = new List<NCAdminExactDrugSummary>();
        if (TryComp<NCPharmacologyComponent>(target, out var pharmacology))
        {
            foreach (var (profileId, exposure) in pharmacology.ActiveDrugs.OrderBy(
                         entry => entry.Key.Id,
                         StringComparer.Ordinal))
            {
                if (!_prototypes.TryIndex(profileId, out NCDrugProfilePrototype? profile))
                    continue;
                exactDrugs.Add(new NCAdminExactDrugSummary(
                    profileId,
                    profile.Category,
                    exposure.Concentration,
                    exposure.CumulativeDose,
                    exposure.LastAdministration));
            }
        }

        snapshot = new NCAdminExactMedicalSnapshot(
            resolutionId,
            medical.SnapshotVersion,
            medical.MedicalRevision,
            _timing.CurTime,
            physiology?.Oxygenation ?? 1f,
            physiology?.TotalPain ?? 0f,
            physiology?.Shock ?? 0f,
            circulation?.BloodVolume ?? 0f,
            circulation?.EffectiveCirculatingVolume ?? 0f,
            circulation?.Perfusion ?? 1f,
            bleeding?.ActiveExternalRate ?? 0f,
            bleeding?.ActiveInternalRate ?? 0f,
            bleeding?.InternalBloodLoss ?? 0f,
            physiology?.Consciousness ?? NCConsciousnessState.Alert,
            physiology?.Condition ?? NCPhysiologicalCondition.Stable,
            exactWounds,
            exactScars,
            exactOrgans,
            exactCyberware,
            exactDrugs,
            FindSurgery(organs));
        return true;
    }

    private NCMedicalVitalsSummary BuildVitals(
        EntityUid target,
        NCDiagnosticCapabilityPrototype policy,
        NCPhysiologyComponent? physiology,
        NCCirculationComponent? circulation,
        NCBleedingComponent? bleeding,
        HashSet<NCDiagnosticWarning> warnings)
    {
        var disclosures = policy.Disclosures;
        var pain = physiology?.PainLevel ?? NCPainLevel.None;
        var shock = physiology?.ShockStage ?? NCShockStage.None;
        var consciousness = physiology?.Consciousness ?? NCConsciousnessState.Alert;
        var condition = physiology?.Condition ?? NCPhysiologicalCondition.Stable;
        var oxygenation = physiology?.Oxygenation ?? 1f;
        var perfusion = circulation?.Perfusion ?? 1f;
        var externalBleed = bleeding?.ActiveExternalRate ?? 0f;
        var internalBleed = bleeding?.ActiveInternalRate ?? 0f;

        if (Has(disclosures, NCDiagnosticDisclosure.SubjectiveSymptoms))
        {
            if (pain != NCPainLevel.None)
                warnings.Add(NCDiagnosticWarning.Pain);
            if (perfusion < 0.75f)
            {
                warnings.Add(NCDiagnosticWarning.Weakness);
                warnings.Add(NCDiagnosticWarning.Dizziness);
            }
            if (oxygenation < 0.9f)
                warnings.Add(NCDiagnosticWarning.RespiratoryDistress);
        }

        if (Has(disclosures, NCDiagnosticDisclosure.ExternalBleeding) && externalBleed > 0f)
            warnings.Add(NCDiagnosticWarning.ActiveExternalBleeding);

        if (Has(disclosures, NCDiagnosticDisclosure.ApproximateVitals) && internalBleed > 0f)
            warnings.Add(Has(disclosures, NCDiagnosticDisclosure.InternalBleeding)
                ? NCDiagnosticWarning.ConfirmedInternalBleeding
                : NCDiagnosticWarning.SuspectedInternalBleeding);

        if (Has(disclosures, NCDiagnosticDisclosure.ApproximateVitals))
        {
            if (oxygenation < 0.9f)
                warnings.Add(NCDiagnosticWarning.Hypoxia);
            if (perfusion < 0.75f)
                warnings.Add(NCDiagnosticWarning.Hypoperfusion);
            if (shock != NCShockStage.None)
                warnings.Add(NCDiagnosticWarning.Shock);
            if (condition >= NCPhysiologicalCondition.Critical)
                warnings.Add(NCDiagnosticWarning.CriticalCondition);
        }

        var hasManual = Has(disclosures, NCDiagnosticDisclosure.ManualVitals);
        var hasApproximate = Has(disclosures, NCDiagnosticDisclosure.ApproximateVitals);
        var bloodVolumeUnits = ResolveBloodVolumeUnits(target, circulation, out var nominalBloodVolumeUnits);
        return new NCMedicalVitalsSummary(
            ToBand(pain),
            hasManual ? ToBand(shock) : NCDiagnosticBand.None,
            consciousness,
            condition,
            hasManual ? circulation?.BloodPressureState : null,
            hasManual ? circulation?.HeartRhythm : null,
            hasApproximate
                ? QuantizeRatio(oxygenation, policy.RatioQuantum)
                : null,
            hasApproximate
                ? Quantize(bloodVolumeUnits ?? 0f, policy.BloodVolumeUnitsQuantum, 1f)
                : null,
            hasApproximate ? nominalBloodVolumeUnits : null,
            hasApproximate
                ? QuantizeRatio(perfusion, policy.RatioQuantum)
                : null,
            Has(disclosures, NCDiagnosticDisclosure.ExternalBleeding)
                ? ToRateBand(externalBleed)
                : NCDiagnosticBand.None,
            Has(disclosures, NCDiagnosticDisclosure.InternalBleeding)
                ? ToRateBand(internalBleed)
                : NCDiagnosticBand.None,
            Has(disclosures, NCDiagnosticDisclosure.ExternalBleeding)
                ? Quantize(externalBleed, policy.BleedRateQuantum, 1000f)
                : null,
            Has(disclosures, NCDiagnosticDisclosure.InternalBleeding)
                ? Quantize(internalBleed, policy.BleedRateQuantum, 1000f)
                : null);
    }

    /// <summary>
    /// Converts the authoritative medical volume into the same solution units shown by the vanilla
    /// bloodstream inspector. The reference solution is read from this body, so species or blood-reagent
    /// overrides cannot silently desynchronize diagnostic presentation from chemistry.
    /// </summary>
    private float? ResolveBloodVolumeUnits(
        EntityUid target,
        NCCirculationComponent? circulation,
        out int? nominalBloodVolumeUnits)
    {
        nominalBloodVolumeUnits = null;
        if (circulation == null ||
            !_prototypes.TryIndex(circulation.Profile, out NCPhysiologyProfilePrototype? physiologyProfile) ||
            physiologyProfile.NominalBloodVolume <= 0f ||
            !float.IsFinite(physiologyProfile.NominalBloodVolume) ||
            !TryComp<BloodstreamComponent>(target, out var bloodstream))
        {
            return null;
        }

        var referenceUnits = (float) bloodstream.BloodReferenceSolution.Volume;
        if (referenceUnits <= 0f || !float.IsFinite(referenceUnits))
            return null;

        nominalBloodVolumeUnits = (int) MathF.Round(referenceUnits);
        var medicalVolume = float.IsFinite(circulation.BloodVolume)
            ? MathF.Max(0f, circulation.BloodVolume)
            : 0f;
        return medicalVolume / physiologyProfile.NominalBloodVolume * referenceUnits;
    }

    private List<NCMedicalRegionSummary> BuildRegions(
        EntityUid target,
        NCMedicalBodyComponent medical,
        NCAnatomyProfilePrototype anatomyProfile,
        NCDiagnosticCapabilityPrototype policy,
        List<OrganEntry> organs,
        List<NCWoundRecord> wounds,
        NCBleedingComponent? bleeding,
        HashSet<NCDiagnosticWarning> warnings)
    {
        var limit = Math.Clamp(policy.MaxRegionRecords, 0, AbsoluteMaxRegions);
        var result = new List<NCMedicalRegionSummary>(Math.Min(limit, anatomyProfile.TargetZones.Count));
        foreach (var zoneId in anatomyProfile.TargetZones)
        {
            if (result.Count >= limit ||
                !_anatomy.TryGetZone((target, medical), zoneId, out _, out var region))
            {
                continue;
            }

            var zoneWounds = wounds.Where(wound => wound.TargetZone == zoneId).ToList();
            var visibleWounds = zoneWounds.Where(IsVisuallyDetectable).ToList();
            var findings = NCDiagnosticFindingFlags.None;
            var bone = FindWorstBone(region, organs);
            var nerveFunction = FindWorstLocalFunction(region, organs);
            var organState = FindWorstOrganFunction(region, organs);
            var internalBleed = zoneWounds.Sum(wound => MathF.Max(0f, wound.InternalBleedRate));
            if (bleeding != null)
            {
                internalBleed += bleeding.Sources
                    .Where(source => source.TargetZone == zoneId && source.Channel == NCBleedingChannel.Internal)
                    .Sum(source => MathF.Max(0f, source.CurrentRate));
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.SubjectiveSymptoms) &&
                zoneWounds.Any(wound => wound.Pain > 0f))
            {
                findings |= NCDiagnosticFindingFlags.Painful;
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.VisibleTrauma))
            {
                if (visibleWounds.Count > 0)
                    findings |= NCDiagnosticFindingFlags.OpenWound;
                if (visibleWounds.Any(wound => wound.WoundType == NCWoundType.Thermal))
                    findings |= NCDiagnosticFindingFlags.Burn;
                if (bone is { State: >= NCBoneState.BrokenDisplaced } || bone?.OpenFracture == true)
                    findings |= NCDiagnosticFindingFlags.Deformity;
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.ExternalBleeding) &&
                zoneWounds.Any(wound => wound.ExternalBleedRate > 0f))
            {
                findings |= NCDiagnosticFindingFlags.ExternalBleeding;
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.ProbableRegions) && zoneWounds.Count > 0)
                findings |= NCDiagnosticFindingFlags.ProbableInjury;

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.StructuralInstability) &&
                bone is { State: >= NCBoneState.Cracked })
            {
                findings |= NCDiagnosticFindingFlags.FractureSuspected;
                warnings.Add(NCDiagnosticWarning.FractureSuspected);
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.Fractures) &&
                bone is { State: >= NCBoneState.Cracked })
            {
                findings |= NCDiagnosticFindingFlags.FractureConfirmed;
                warnings.Add(NCDiagnosticWarning.FractureConfirmed);
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.InternalBleeding) && internalBleed > 0f)
                findings |= NCDiagnosticFindingFlags.InternalBleeding;

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.Organs))
            {
                if (organState.Impaired)
                {
                    findings |= NCDiagnosticFindingFlags.OrganImpairment;
                    warnings.Add(NCDiagnosticWarning.OrganImpairment);
                }
                if (organState.Failing)
                {
                    findings |= NCDiagnosticFindingFlags.OrganFailure;
                    warnings.Add(NCDiagnosticWarning.OrganFailure);
                }
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.ManualVitals) && nerveFunction < 0.9f)
                findings |= NCDiagnosticFindingFlags.NeurologicalDeficit;

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.ForeignBodies) &&
                zoneWounds.Any(wound => wound.EmbeddedObjectId != null))
            {
                findings |= NCDiagnosticFindingFlags.ForeignBody;
                warnings.Add(NCDiagnosticWarning.ForeignBody);
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.Infection) &&
                zoneWounds.Any(wound => wound.Infection > 0f))
            {
                findings |= NCDiagnosticFindingFlags.Infection;
                warnings.Add(NCDiagnosticWarning.Infection);
            }

            if (Has(policy.Disclosures, NCDiagnosticDisclosure.SurgicalState) &&
                TryComp<NCSurgicalSiteComponent>(region, out var site) &&
                site.AccessDepth != NCSurgicalAccessDepth.Closed)
            {
                findings |= NCDiagnosticFindingFlags.SurgicalAccess;
            }

            var visibleOnly = Has(policy.Disclosures, NCDiagnosticDisclosure.VisibleTrauma) &&
                              !Has(policy.Disclosures, NCDiagnosticDisclosure.ProbableRegions);
            var disclosedWounds = visibleOnly ? visibleWounds : zoneWounds;
            var burden = disclosedWounds.Count == 0
                ? NCDiagnosticBand.None
                : disclosedWounds.Max(wound => ToBand(wound.Severity));
            byte? woundCount = Has(policy.Disclosures, NCDiagnosticDisclosure.VisibleTrauma)
                ? (byte) Math.Min(byte.MaxValue, disclosedWounds.Count)
                : null;

            result.Add(new NCMedicalRegionSummary(
                zoneId,
                findings,
                burden,
                woundCount,
                Has(policy.Disclosures, NCDiagnosticDisclosure.Fractures) ? bone?.State : null,
                ToFunctionBand(nerveFunction)));
        }

        return result;
    }

    private List<NCMedicalWoundSummary> BuildWounds(
        NCDiagnosticCapabilityPrototype policy,
        List<NCWoundRecord> wounds,
        out int omitted,
        HashSet<NCDiagnosticWarning> warnings)
    {
        omitted = 0;
        var detailed = Has(policy.Disclosures, NCDiagnosticDisclosure.DetailedWounds);
        var visible = Has(policy.Disclosures, NCDiagnosticDisclosure.VisibleTrauma);
        if (!detailed && !visible)
            return [];

        var eligible = detailed ? wounds : wounds.Where(IsVisuallyDetectable).ToList();
        var limit = Math.Clamp(policy.MaxWoundRecords, 0, AbsoluteMaxWounds);
        var result = new List<NCMedicalWoundSummary>(Math.Min(limit, eligible.Count));
        foreach (var wound in eligible.Take(limit))
        {
            var hasInternal = Has(policy.Disclosures, NCDiagnosticDisclosure.InternalBleeding);
            var hasInfection = Has(policy.Disclosures, NCDiagnosticDisclosure.Infection);
            var hasForeignBody = Has(policy.Disclosures, NCDiagnosticDisclosure.ForeignBodies);
            result.Add(new NCMedicalWoundSummary(
                wound.TargetZone,
                detailed ? (ProtoId<NCWoundPrototype>?) wound.Prototype : null,
                wound.WoundType,
                wound.Severity,
                detailed ? wound.Stage : null,
                detailed ? wound.TissueDepth : null,
                Has(policy.Disclosures, NCDiagnosticDisclosure.ExternalBleeding)
                    ? ToRateBand(wound.ExternalBleedRate)
                    : NCDiagnosticBand.None,
                hasInternal ? ToRateBand(wound.InternalBleedRate) : NCDiagnosticBand.None,
                hasInfection ? ToFractionBand(wound.Infection) : NCDiagnosticBand.None,
                hasForeignBody && wound.EmbeddedObjectId != null,
                wound.VisualX,
                wound.VisualY,
                wound.VisualDirection));

            if (hasForeignBody && wound.EmbeddedObjectId != null)
                warnings.Add(NCDiagnosticWarning.ForeignBody);
            if (hasInfection && wound.Infection > 0f)
                warnings.Add(NCDiagnosticWarning.Infection);
        }

        omitted = eligible.Count - result.Count;
        return result;
    }

    private List<NCMedicalOrganSummary> BuildOrgans(
        NCDiagnosticCapabilityPrototype policy,
        List<OrganEntry> organs,
        out int omitted,
        HashSet<NCDiagnosticWarning> warnings)
    {
        omitted = 0;
        if (!Has(policy.Disclosures, NCDiagnosticDisclosure.Organs))
            return [];

        var eligible = organs.Where(organ => HasComp<NCOrganFunctionComponent>(organ.Uid)).ToList();
        var limit = Math.Clamp(policy.MaxOrganRecords, 0, AbsoluteMaxOrgans);
        var result = new List<NCMedicalOrganSummary>(Math.Min(limit, eligible.Count));
        foreach (var organ in eligible.Take(limit))
        {
            var function = Comp<NCOrganFunctionComponent>(organ.Uid);
            var integrity = CompOrNull<NCOrganIntegrityComponent>(organ.Uid);
            var failing = function.CurrentCapacity <= function.FailingThreshold;
            var impaired = function.CurrentCapacity <= function.ImpairedThreshold;
            var rejectionBand = NCDiagnosticBand.None;
            if (TryComp<NCTransplantRejectionComponent>(organ.Uid, out var rejection) &&
                _prototypes.TryIndex(rejection.Profile, out NCTransplantRejectionProfilePrototype? rejectionProfile))
            {
                rejectionBand = ToFractionBand(SafeRatio(rejection.Burden, rejectionProfile.MaximumBurden));
                if (rejection.Stage >= NCTransplantRejectionStage.Active)
                    warnings.Add(NCDiagnosticWarning.TransplantRejection);
            }
            if (failing)
                warnings.Add(NCDiagnosticWarning.OrganFailure);
            else if (impaired)
                warnings.Add(NCDiagnosticWarning.OrganImpairment);

            result.Add(new NCMedicalOrganSummary(
                organ.Category,
                function.FunctionType,
                function.Vital,
                integrity == null
                    ? NCDiagnosticBand.None
                    : ToFunctionBand(SafeRatio(integrity.CurrentIntegrity, integrity.MaxIntegrity)),
                ToOrganFunctionBand(function),
                failing,
                rejectionBand));
        }

        omitted = eligible.Count - result.Count;
        return result;
    }

    private List<NCMedicalCyberwareSummary> BuildCyberware(
        EntityUid body,
        NCDiagnosticCapabilityPrototype policy,
        TimeSpan observedAt,
        out int omitted,
        HashSet<NCDiagnosticWarning> warnings)
    {
        omitted = 0;
        if (!Has(policy.Disclosures, NCDiagnosticDisclosure.Cyberware))
            return [];

        var modules = _cyberware.CollectCyberware(body)
            .Where(uid => TryComp<NCCyberwareComponent>(uid, out _))
            .OrderBy(uid => Comp<NCCyberwareComponent>(uid).Profile.Id, StringComparer.Ordinal)
            .ThenBy(uid => Comp<NCCyberwareComponent>(uid).StableInstanceId)
            .ToList();
        var limit = Math.Clamp(policy.MaxCyberwareRecords, 0, AbsoluteMaxCyberware);
        var result = new List<NCMedicalCyberwareSummary>(Math.Min(limit, modules.Count));
        foreach (var moduleUid in modules.Take(limit))
        {
            var module = Comp<NCCyberwareComponent>(moduleUid);
            if (!_prototypes.TryIndex(module.Profile, out NCCyberwarePrototype? profile) ||
                !_cyberware.TryResolveSemanticPlacement(body, moduleUid, out var hostCategory, out var slot))
            {
                continue;
            }

            TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity);
            var empDisabled = module.EmpDisabledUntil > observedAt;
            if (!module.Operational)
                warnings.Add(NCDiagnosticWarning.CyberwareOffline);
            if (module.Heat >= profile.HeatDeratingThreshold)
                warnings.Add(NCDiagnosticWarning.CyberwareOverheated);
            if (empDisabled)
                warnings.Add(NCDiagnosticWarning.CyberwareEmpDisabled);
            if (module.NeuralState is NCCyberwareNeuralState.Degraded or
                NCCyberwareNeuralState.Disconnected or NCCyberwareNeuralState.Disrupted)
            {
                warnings.Add(NCDiagnosticWarning.NeuralInterfaceFault);
            }

            result.Add(new NCMedicalCyberwareSummary(
                module.Profile,
                profile.Form,
                hostCategory,
                slot,
                integrity == null
                    ? NCDiagnosticBand.None
                    : ToFunctionBand(SafeRatio(integrity.CurrentIntegrity, integrity.MaxIntegrity)),
                ToFunctionBand(module.FunctionalEfficiency),
                profile.PowerCapacity <= 0f
                    ? NCDiagnosticBand.None
                    : ToFunctionBand(SafeRatio(module.CurrentPower, profile.PowerCapacity)),
                ToFractionBand(SafeRatio(module.Heat, profile.HeatCapacity)),
                module.NeuralState,
                module.Operational,
                empDisabled));
        }

        omitted = modules.Count - result.Count;
        return result;
    }

    private List<NCMedicalDrugSummary> BuildDrugs(
        EntityUid body,
        NCDiagnosticCapabilityPrototype policy,
        out int omitted,
        HashSet<NCDiagnosticWarning> warnings)
    {
        omitted = 0;
        if (!Has(policy.Disclosures, NCDiagnosticDisclosure.Drugs) ||
            !TryComp<NCPharmacologyComponent>(body, out var state))
        {
            return [];
        }

        var exposures = state.ActiveDrugs
            .Where(entry => _prototypes.HasIndex(entry.Key))
            .OrderBy(entry => entry.Key.Id, StringComparer.Ordinal)
            .ToList();
        var limit = Math.Clamp(policy.MaxDrugRecords, 0, AbsoluteMaxDrugs);
        var result = new List<NCMedicalDrugSummary>(Math.Min(limit, exposures.Count));
        foreach (var (profileId, exposure) in exposures.Take(limit))
        {
            var profile = _prototypes.Index(profileId);
            result.Add(new NCMedicalDrugSummary(
                profileId,
                profile.Category,
                Quantize(exposure.Concentration, policy.DrugConcentrationQuantum, 1000f),
                exposure.Concentration >= profile.MinimumEffectiveConcentration &&
                exposure.Concentration <= profile.ToxicConcentration,
                exposure.Concentration > profile.ToxicConcentration));
        }

        var modifiers = _pharmacology.GetModifiers(body);
        if (modifiers.Sedation >= 0.25f)
            warnings.Add(NCDiagnosticWarning.Sedated);
        if (modifiers.Anesthesia >= 0.5f)
            warnings.Add(NCDiagnosticWarning.Anesthetized);
        if (modifiers.OverdoseFraction > 0f)
            warnings.Add(NCDiagnosticWarning.DrugOverdose);

        omitted = exposures.Count - result.Count;
        return result;
    }

    private NCMedicalSurgerySummary? BuildSurgery(
        NCDiagnosticCapabilityPrototype policy,
        List<OrganEntry> organs,
        HashSet<NCDiagnosticWarning> warnings)
    {
        if (!Has(policy.Disclosures, NCDiagnosticDisclosure.SurgicalState))
            return null;

        var surgery = FindSurgery(organs);
        if (surgery != null)
            warnings.Add(NCDiagnosticWarning.ActiveSurgery);
        return surgery;
    }

    private NCMedicalSurgerySummary? FindSurgery(List<OrganEntry> organs)
    {
        foreach (var organ in organs)
        {
            if (!TryComp<NCSurgerySessionComponent>(organ.Uid, out var session) ||
                !TryComp<NCSurgicalSiteComponent>(organ.Uid, out var site))
            {
                continue;
            }

            return new NCMedicalSurgerySummary(
                organ.Category,
                session.Procedure,
                session.CurrentStepIndex,
                session.State,
                site.AccessDepth);
        }

        return null;
    }

    private bool TryResolveCapability(
        EntityUid? tool,
        NCDiagnosticTier requestedTier,
        out ProtoId<NCDiagnosticCapabilityPrototype> capabilityId,
        out NCDiagnosticCapabilityPrototype capability,
        out NCDiagnosticAccessFailure failure)
    {
        capabilityId = default;
        capability = default!;
        failure = NCDiagnosticAccessFailure.None;
        if (tool is { } toolUid)
        {
            if (!Exists(toolUid) ||
                !TryComp<NCDiagnosticCapabilityComponent>(toolUid, out var observed) ||
                !_prototypes.TryIndex(
                    observed.Capability,
                    out NCDiagnosticCapabilityPrototype? observedCapability))
            {
                failure = NCDiagnosticAccessFailure.CapabilityUnavailable;
                return false;
            }

            capability = observedCapability!;
            if (capability.Tier != requestedTier)
            {
                failure = NCDiagnosticAccessFailure.TierUnavailable;
                return false;
            }

            capabilityId = observed.Capability;
            return true;
        }

        if (!_unassisted.TryGetValue(requestedTier, out capabilityId))
        {
            CacheUnassistedCapabilities();
            if (!_unassisted.TryGetValue(requestedTier, out capabilityId))
            {
                failure = requestedTier > NCDiagnosticTier.Manual
                    ? NCDiagnosticAccessFailure.ToolRequired
                    : NCDiagnosticAccessFailure.CapabilityUnavailable;
                return false;
            }
        }

        if (!_prototypes.TryIndex(
                capabilityId,
                out NCDiagnosticCapabilityPrototype? unassistedCapability))
        {
            failure = NCDiagnosticAccessFailure.CapabilityUnavailable;
            return false;
        }

        capability = unassistedCapability!;
        return true;
    }

    private void OnSnapshotRequest(NCDiagnosticSnapshotRequest request, EntitySessionEventArgs args)
    {
        // The client nonce is only for UI matching. The authoritative correlation is always minted here.
        var resolutionId = NCActionResolutionId.New();
        if (_rateLimit.CountAction(args.SenderSession, RequestRateLimitKey) != RateLimitStatus.Allowed)
        {
            SendResponse(
                args.SenderSession,
                request.RequestId,
                resolutionId,
                false,
                NCDiagnosticAccessFailure.RateLimited,
                null);
            return;
        }

        if (args.SenderSession.AttachedEntity is not { Valid: true } actor || !Exists(actor))
        {
            SendResponse(
                args.SenderSession,
                request.RequestId,
                resolutionId,
                false,
                NCDiagnosticAccessFailure.ActorUnavailable,
                null);
            return;
        }

        var target = GetEntity(request.Target);
        EntityUid? tool = request.Tool is { } netTool ? GetEntity(netTool) : null;
        var accepted = TryBuildForActor(
            resolutionId,
            actor,
            target,
            tool,
            request.Tier,
            out var snapshot,
            out var failure);
        SendResponse(
            args.SenderSession,
            request.RequestId,
            resolutionId,
            accepted,
            failure,
            accepted ? snapshot : null,
            target,
            tool,
            request.Tier);
    }

    private void OnAfterInteract(Entity<NCDiagnosticCapabilityComponent> entity, ref AfterInteractEvent args)
    {
        // Vanilla analyzers own their scan delay, power use, sounds and primary UI. The completion event below
        // supplies the authoritative NC detail only after that existing workflow succeeds.
        if (HasComp<HealthAnalyzerComponent>(entity.Owner))
            return;

        if (args.Handled || args.Target is not { } target || !args.CanReach ||
            !_prototypes.TryIndex(entity.Comp.Capability, out NCDiagnosticCapabilityPrototype? capability) ||
            !capability.RequiresHeld ||
            !TryComp<ActorComponent>(args.User, out var actor))
        {
            return;
        }

        var resolutionId = NCActionResolutionId.New();
        var accepted = TryBuildForActor(
            resolutionId,
            args.User,
            target,
            entity.Owner,
            capability.Tier,
            out var snapshot,
            out var failure);
        SendResponse(
            actor.PlayerSession,
            0,
            resolutionId,
            accepted,
            failure,
            accepted ? snapshot : null,
            target,
            entity.Owner,
            capability.Tier);
        args.Handled = accepted;
    }

    /// <summary>
    /// Reuses the completed vanilla health-analyzer scan instead of starting a second instant NC interaction.
    /// </summary>
    private void OnAnalyzerScanCompleted(
        Entity<NCDiagnosticCapabilityComponent> entity,
        ref NCHealthAnalyzerScanCompletedEvent args)
    {
        if (!_prototypes.TryIndex(entity.Comp.Capability, out NCDiagnosticCapabilityPrototype? capability) ||
            !TryComp<ActorComponent>(args.User, out var actor))
        {
            return;
        }

        var resolutionId = NCActionResolutionId.New();
        var accepted = TryBuildForActor(
            resolutionId,
            args.User,
            args.Target,
            entity.Owner,
            capability.Tier,
            out var snapshot,
            out var failure);
        SendResponse(
            actor.PlayerSession,
            0,
            resolutionId,
            accepted,
            failure,
            accepted ? snapshot : null,
            args.Target,
            entity.Owner,
            capability.Tier);
    }

    /// <summary>
    /// Adds diagnostics to the existing scanner's verb set. The patient is derived from its authoritative
    /// container; neither the client nor a second NC scanner entity can substitute another target.
    /// </summary>
    private void OnGetScannerVerbs(
        Entity<NCDiagnosticCapabilityComponent> entity,
        ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess ||
            !args.CanInteract ||
            !TryComp<MedicalScannerComponent>(entity.Owner, out var scanner) ||
            scanner.BodyContainer.ContainedEntity is not { } patient ||
            !_prototypes.TryIndex(entity.Comp.Capability, out NCDiagnosticCapabilityPrototype? capability) ||
            !TryComp<ActorComponent>(args.User, out var actor))
        {
            return;
        }

        var user = args.User;
        var session = actor.PlayerSession;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("nc-medical-diagnostics-scanner-verb"),
            Priority = 2,
            Act = () =>
            {
                var resolutionId = NCActionResolutionId.New();
                var accepted = TryBuildForActor(
                    resolutionId,
                    user,
                    patient,
                    entity.Owner,
                    capability.Tier,
                    out var snapshot,
                    out var failure);
                SendResponse(
                    session,
                    0,
                    resolutionId,
                    accepted,
                    failure,
                    accepted ? snapshot : null,
                    patient,
                    entity.Owner,
                    capability.Tier);
            },
        });
    }

    /// <summary>
    /// Exposes the unassisted diagnostic tiers through normal server verbs. The patient is the entity that
    /// owns this component, so the client never supplies a target identity for visual or manual examination.
    /// </summary>
    private void OnGetPatientDiagnosticVerbs(
        Entity<NCMedicalBodyComponent> entity,
        ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !TryComp<ActorComponent>(args.User, out var actor))
            return;

        var user = args.User;
        var patient = entity.Owner;
        var session = actor.PlayerSession;
        var verbs = args.Verbs;

        if (user == patient)
        {
            AddDiagnosticVerb(
                "nc-medical-diagnostics-self-assessment-verb",
                NCDiagnosticTier.SelfSensations,
                3);
        }

        AddDiagnosticVerb(
            "nc-medical-diagnostics-visual-examination-verb",
            NCDiagnosticTier.Visual,
            1);

        if (args.CanInteract)
        {
            AddDiagnosticVerb(
                "nc-medical-diagnostics-manual-examination-verb",
                NCDiagnosticTier.Manual,
                2);
        }

        return;

        void AddDiagnosticVerb(string text, NCDiagnosticTier tier, int priority)
        {
            verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString(text),
                Priority = priority,
                Act = () =>
                {
                    var resolutionId = NCActionResolutionId.New();
                    var accepted = TryBuildForActor(
                        resolutionId,
                        user,
                        patient,
                        null,
                        tier,
                        out var snapshot,
                        out var failure);
                    SendResponse(
                        session,
                        0,
                        resolutionId,
                        accepted,
                        failure,
                        accepted ? snapshot : null,
                        patient,
                        null,
                        tier);
                },
            });
        }
    }

    private void SendResponse(
        ICommonSession session,
        uint requestId,
        NCActionResolutionId resolutionId,
        bool accepted,
        NCDiagnosticAccessFailure failure,
        NCMedicalSnapshot? snapshot,
        EntityUid? target = null,
        EntityUid? tool = null,
        NCDiagnosticTier tier = NCDiagnosticTier.SelfSensations)
    {
        // This overload targets exactly one session. Medical snapshots must never use a PVS or broadcast filter.
        var netTarget = target is { Valid: true } targetUid && Exists(targetUid)
            ? GetNetEntity(targetUid)
            : (NetEntity?) null;
        var netTool = tool is { Valid: true } toolUid && Exists(toolUid)
            ? GetNetEntity(toolUid)
            : (NetEntity?) null;
        RaiseNetworkEvent(
            new NCDiagnosticSnapshotResponse(
                requestId,
                resolutionId,
                accepted,
                failure,
                netTarget,
                netTool,
                tier,
                snapshot),
            session);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        CacheUnassistedCapabilities();
    }

    private void CacheUnassistedCapabilities()
    {
        _unassisted.Clear();
        foreach (var group in _prototypes.EnumeratePrototypes<NCDiagnosticCapabilityPrototype>()
                     .Where(policy => policy.UnassistedDefault)
                     .GroupBy(policy => policy.Tier))
        {
            var defaults = group.OrderBy(policy => policy.ID, StringComparer.Ordinal).ToArray();
            if (defaults.Length != 1)
            {
                Log.Error($"Diagnostic tier {group.Key} has {defaults.Length} unassisted defaults; exactly one is required.");
                continue;
            }

            _unassisted[group.Key] = defaults[0].ID;
        }
    }

    private List<OrganEntry> CollectOrgans(EntityUid body)
    {
        var result = new List<OrganEntry>();
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return result;

        foreach (var uid in bodyComponent.Organs.ContainedEntities)
        {
            if (TryComp<OrganComponent>(uid, out var organ) && organ.Category is { } category)
                result.Add(new OrganEntry(uid, category));
        }

        result.Sort((left, right) => StringComparer.Ordinal.Compare(left.Category.Id, right.Category.Id));
        return result;
    }

    private List<NCWoundRecord> CollectWounds(EntityUid body)
    {
        if (!TryComp<NCWoundContainerComponent>(body, out var container))
            return [];

        return container.Wounds.Values
            .OrderBy(wound => wound.TargetZone.Id, StringComparer.Ordinal)
            .ThenBy(wound => wound.CreatedAt)
            .ThenBy(wound => wound.Prototype.Id, StringComparer.Ordinal)
            .ThenBy(wound => wound.WoundId)
            .ToList();
    }

    private NCBoneComponent? FindWorstBone(EntityUid region, List<OrganEntry> organs)
    {
        NCBoneComponent? worst = null;
        foreach (var organ in organs)
        {
            if (!IsWithinRegion(organ.Uid, region) || !TryComp<NCBoneComponent>(organ.Uid, out var bone))
                continue;
            if (worst == null || bone.State > worst.State)
                worst = bone;
        }
        return worst;
    }

    private float FindWorstLocalFunction(EntityUid region, List<OrganEntry> organs)
    {
        var value = 1f;
        foreach (var organ in organs)
        {
            if (!IsWithinRegion(organ.Uid, region))
                continue;
            if (TryComp<NCNerveComponent>(organ.Uid, out var nerve))
                value = MathF.Min(value, MathF.Min(nerve.NeuralIntegrity, nerve.MotorFunction));
            if (TryComp<NCTendonComponent>(organ.Uid, out var tendon))
                value = MathF.Min(value, tendon.Ruptured ? 0f : tendon.Integrity);
            if (TryComp<NCJointComponent>(organ.Uid, out var joint))
                value = MathF.Min(value, joint.Dislocated ? 0f : joint.Stability);
        }
        return Math.Clamp(value, 0f, 1f);
    }

    private OrganState FindWorstOrganFunction(EntityUid region, List<OrganEntry> organs)
    {
        var impaired = false;
        var failing = false;
        foreach (var organ in organs)
        {
            if (!IsWithinRegion(organ.Uid, region) || !TryComp<NCOrganFunctionComponent>(organ.Uid, out var function))
                continue;
            impaired |= function.CurrentCapacity <= function.ImpairedThreshold;
            failing |= function.CurrentCapacity <= function.FailingThreshold;
        }
        return new OrganState(impaired, failing);
    }

    private bool IsWithinRegion(EntityUid organ, EntityUid region)
    {
        EntityUid? current = organ;
        var remaining = OrganTraversalLimit;
        while (current is { } currentUid && remaining-- > 0)
        {
            if (currentUid == region)
                return true;
            current = CompOrNull<ChildOrganComponent>(currentUid)?.Parent;
        }
        return false;
    }

    private static bool IsVisuallyDetectable(NCWoundRecord wound)
    {
        return wound.ClosureState != NCWoundClosureState.Healed &&
               (wound.TissueDepth <= NCTissueDepth.SoftTissue ||
                wound.ExternalBleedRate > 0f ||
                wound.EmbeddedObjectId != null ||
                wound.WoundType == NCWoundType.Thermal);
    }

    private static bool Has(NCDiagnosticDisclosure actual, NCDiagnosticDisclosure requested)
    {
        return (actual & requested) == requested;
    }

    private static NCDiagnosticBand ToBand(NCPainLevel value)
    {
        return value switch
        {
            NCPainLevel.None => NCDiagnosticBand.None,
            NCPainLevel.Mild => NCDiagnosticBand.Low,
            NCPainLevel.Moderate => NCDiagnosticBand.Moderate,
            NCPainLevel.Severe => NCDiagnosticBand.High,
            _ => NCDiagnosticBand.Critical,
        };
    }

    private static NCDiagnosticBand ToBand(NCShockStage value)
    {
        return value switch
        {
            NCShockStage.None => NCDiagnosticBand.None,
            NCShockStage.Compensated => NCDiagnosticBand.Low,
            NCShockStage.Moderate => NCDiagnosticBand.Moderate,
            NCShockStage.Severe => NCDiagnosticBand.High,
            _ => NCDiagnosticBand.Critical,
        };
    }

    private static NCDiagnosticBand ToBand(NCWoundSeverity value)
    {
        return value switch
        {
            NCWoundSeverity.Minor => NCDiagnosticBand.Low,
            NCWoundSeverity.Moderate => NCDiagnosticBand.Moderate,
            NCWoundSeverity.Severe => NCDiagnosticBand.High,
            _ => NCDiagnosticBand.Critical,
        };
    }

    private static NCDiagnosticBand ToRateBand(float rate)
    {
        if (rate <= 0f)
            return NCDiagnosticBand.None;
        if (rate < 0.5f)
            return NCDiagnosticBand.Low;
        if (rate < 2f)
            return NCDiagnosticBand.Moderate;
        return rate < 5f ? NCDiagnosticBand.High : NCDiagnosticBand.Critical;
    }

    private static NCDiagnosticBand ToFractionBand(float value)
    {
        if (value <= 0f)
            return NCDiagnosticBand.None;
        if (value < 0.25f)
            return NCDiagnosticBand.Low;
        if (value < 0.5f)
            return NCDiagnosticBand.Moderate;
        return value < 0.75f ? NCDiagnosticBand.High : NCDiagnosticBand.Critical;
    }

    private static NCDiagnosticBand ToFunctionBand(float value)
    {
        if (value >= 0.9f)
            return NCDiagnosticBand.None;
        if (value >= 0.7f)
            return NCDiagnosticBand.Low;
        if (value >= 0.4f)
            return NCDiagnosticBand.Moderate;
        return value >= 0.1f ? NCDiagnosticBand.High : NCDiagnosticBand.Critical;
    }

    private static NCDiagnosticBand ToOrganFunctionBand(NCOrganFunctionComponent function)
    {
        if (function.CurrentCapacity > function.ImpairedThreshold)
            return NCDiagnosticBand.None;
        if (function.CurrentCapacity > function.FailingThreshold)
            return NCDiagnosticBand.Moderate;
        if (function.CurrentCapacity > function.NonFunctionalThreshold)
            return NCDiagnosticBand.High;
        return NCDiagnosticBand.Critical;
    }

    private static float SafeRatio(float current, float maximum)
    {
        return maximum > 0f && float.IsFinite(current) && float.IsFinite(maximum)
            ? Math.Clamp(current / maximum, 0f, 1f)
            : 0f;
    }

    private static NCDiagnosticNumericRange QuantizeRatio(float value, float quantum)
    {
        return Quantize(Math.Clamp(value, 0f, 1f), quantum, 1000f);
    }

    private static NCDiagnosticNumericRange Quantize(float value, float quantum, float scale)
    {
        value = float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
        quantum = float.IsFinite(quantum) && quantum > 0f ? quantum : 1f;
        scale = float.IsFinite(scale) && scale > 0f ? scale : 1f;
        var lower = MathF.Floor(value / quantum) * quantum;
        var upper = lower + quantum;
        return new NCDiagnosticNumericRange(
            (int) MathF.Round(lower * scale),
            (int) MathF.Round(upper * scale));
    }

    private readonly record struct OrganEntry(EntityUid Uid, ProtoId<OrganCategoryPrototype> Category);
    private readonly record struct OrganState(bool Impaired, bool Failing);
}
