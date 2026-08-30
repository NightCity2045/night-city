// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    private bool ValidateIncision(NCSurgicalIncisionStateSnapshot incision)
    {
        if (incision == null || incision.IncisionId == Guid.Empty ||
            incision.WoundId is { } woundId && woundId == Guid.Empty ||
            incision.LinkedWoundIds == null ||
            incision.LinkedWoundIds.Any(linked => linked == Guid.Empty || linked == incision.WoundId) ||
            incision.LinkedWoundIds.Distinct().Count() != incision.LinkedWoundIds.Count ||
            !_prototypes.HasIndex(incision.TargetZone) ||
            !Enum.IsDefined(incision.Surface) ||
            incision.Polyline == null || incision.Polyline.Count is < 2 or > MaxIncisionPoints ||
            incision.Polyline.Any(point =>
                !float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
                point.X is < 0f or > 1f || point.Y is < 0f or > 1f) ||
            !Finite(incision.Depth, 0f, 1f) ||
            !Finite(incision.Width, 0f, 1f) ||
            !Finite(incision.EdgeSeparation, 0f, 1f) ||
            !Finite(incision.Contamination, 0f, 1f) ||
            !incision.SourceResolutionId.IsValid ||
            !ValidTime(incision.CreatedAt) || !ValidTime(incision.ModifiedAt) ||
            incision.ModifiedAt < incision.CreatedAt)
        {
            return false;
        }

        return true;
    }

    private static NCSurgicalIncisionStateSnapshot CloneIncision(NCSurgicalIncisionStateSnapshot incision)
    {
        return new NCSurgicalIncisionStateSnapshot
        {
            IncisionId = incision.IncisionId,
            WoundId = incision.WoundId,
            LinkedWoundIds = [.. incision.LinkedWoundIds],
            TargetZone = incision.TargetZone,
            Surface = incision.Surface,
            Polyline = [.. incision.Polyline],
            Depth = incision.Depth,
            Width = incision.Width,
            EdgeSeparation = incision.EdgeSeparation,
            Contamination = incision.Contamination,
            Sutured = incision.Sutured,
            BoneCasingOpen = incision.BoneCasingOpen,
            SourceResolutionId = incision.SourceResolutionId,
            CreatedAt = incision.CreatedAt,
            ModifiedAt = incision.ModifiedAt,
        };
    }

    /// <summary>
    /// Rejects corrupt deserialized object graphs before migration dereferences any required collection.
    /// This is deliberately separate from semantic validation so malformed storage is a normal false
    /// result, never an exception that can interrupt character loading.
    /// </summary>
    private static bool HasNonNullSnapshotShape(NCMedicalStateSnapshot snapshot)
    {
        if (snapshot.MissingOrganCategories == null ||
            snapshot.Organs == null ||
            snapshot.CyberwareModules == null ||
            snapshot.Circulation == null ||
            snapshot.Physiology == null ||
            snapshot.Circulation.TransfusionCompatibility == null ||
            snapshot.CyberwareModules.Any(module => module == null) ||
            snapshot.Organs.Values.Any(organ => organ == null ||
                organ.SurgicalSite is { TemporaryDevices: null } or { Incisions: null } ||
                organ.SurgicalSite?.Incisions.Any(incision => incision == null || incision.Polyline == null) == true))
        {
            return false;
        }

        if (snapshot.Wounds is { } wounds &&
            (wounds.Records == null ||
             wounds.Scars == null ||
             wounds.OverflowTraumaByZone == null ||
             wounds.Records.Values.Any(record => record == null) ||
             wounds.Scars.Any(record => record == null)))
        {
            return false;
        }

        if (snapshot.Bleeding is { } bleeding &&
            (bleeding.Sources == null ||
             bleeding.OverflowInternalRatesByRegion == null ||
             bleeding.Sources.Any(source => source == null)))
        {
            return false;
        }

        if (snapshot.Treatment is { } treatment &&
            (treatment.WoundTreatments == null ||
             treatment.Tourniquets == null ||
             treatment.WoundTreatments.Values.Any(record => record == null) ||
             treatment.Tourniquets.Values.Any(record => record == null || record.CoveredWoundIds == null)))
        {
            return false;
        }

        if (snapshot.Systemic is { Burdens: null })
            return false;

        if (snapshot.Pharmacology is { ActiveDrugs: null } ||
            snapshot.Pharmacology?.ActiveDrugs.Any(exposure => exposure == null) == true)
            return false;

        return true;
    }

    private bool ValidateCurrentSnapshot(EntityUid character, NCMedicalStateSnapshot snapshot)
    {
        if (snapshot.Version != NCMedicalSnapshotVersions.Current ||
            !ValidTime(snapshot.CapturedAt) ||
            !TryGetBodyContext(character, out var medical, out _, out var initial) ||
            snapshot.AnatomyProfile != medical.Profile ||
            !_prototypes.HasIndex(snapshot.AnatomyProfile) ||
            snapshot.MissingOrganCategories.Count > MaxMissingOrganCategories ||
            snapshot.Organs.Count > MaxOrganRecords ||
            snapshot.CyberwareModules.Count > MaxCyberwareRecords ||
            !TryBuildParentMap(initial, out var parents))
        {
            return false;
        }

        var initialOrgans = initial.Organs;
        if (initialOrgans.Count > MaxOrganRecords)
            return false;
        var expected = initialOrgans.Keys.ToHashSet();
        var missing = snapshot.MissingOrganCategories.ToHashSet();
        if (missing.Count != snapshot.MissingOrganCategories.Count ||
            !missing.IsSubsetOf(expected) ||
            snapshot.Organs.Keys.Any(category => !expected.Contains(category)) ||
            !snapshot.Organs.Keys.ToHashSet().SetEquals(expected.Except(missing)))
        {
            return false;
        }

        // Missing anatomy is subtree-closed. A child cannot remain in a body after its live parent was
        // removed, and the root organ cannot be absent while the character entity still represents a body.
        foreach (var root in expected.Where(category => !parents.ContainsKey(category)))
        {
            if (missing.Contains(root))
                return false;
        }

        if (parents.Any(edge => missing.Contains(edge.Value) && !missing.Contains(edge.Key)))
            return false;

        foreach (var (category, state) in snapshot.Organs)
        {
            if (!_prototypes.HasIndex(category) ||
                string.IsNullOrWhiteSpace(state.EntityPrototype) ||
                state.EntityPrototype.Length > MaxPersistentPrototypeIdLength ||
                !_prototypes.TryIndex<EntityPrototype>(state.EntityPrototype, out var organPrototype) ||
                !organPrototype.TryComp<OrganComponent>(out var organDefinition, _componentFactory) ||
                organDefinition.Category != category ||
                !ValidateOrganState(organPrototype, state))
            {
                return false;
            }
        }

        var cyberwareIds = new HashSet<Guid>();
        var occupiedSlots = new HashSet<(ProtoId<OrganCategoryPrototype>, ProtoId<NCCyberwareSlotPrototype>)>();
        foreach (var state in snapshot.CyberwareModules)
        {
            if (!_cyberware.ValidatePersistentState(state, true) ||
                !cyberwareIds.Add(state.StableInstanceId) ||
                state.HostCategory is not { } hostCategory || state.Slot is not { } slot ||
                missing.Contains(hostCategory) || !snapshot.Organs.TryGetValue(hostCategory, out var hostState) ||
                !_prototypes.TryIndex<EntityPrototype>(hostState.EntityPrototype, out var hostPrototype) ||
                !hostPrototype.TryComp<NCCyberwareSlotHostComponent>(out var slotHost, _componentFactory) ||
                !slotHost.Slots.Contains(slot) || !occupiedSlots.Add((hostCategory, slot)))
            {
                return false;
            }
        }

        foreach (var organ in snapshot.Organs.Values)
        {
            if (organ.Cyberware is { } cyberware && !cyberwareIds.Add(cyberware.StableInstanceId))
                return false;
        }

        if (!ValidateWounds(snapshot, initial, missing) ||
            !ValidateBleeding(snapshot, initial, missing) ||
            !ValidateTreatment(snapshot) ||
            !ValidateEmergencyCare(snapshot.EmergencyCare) ||
            !ValidateSystemic(snapshot.Systemic) ||
            snapshot.Pharmacology is { } pharmacology &&
            !_pharmacology.ValidatePersistentState(pharmacology) ||
            !ValidateCirculation(snapshot.Circulation) ||
            !ValidatePhysiology(snapshot.Physiology))
        {
            return false;
        }

        return true;
    }

    private bool ValidateOrganState(EntityPrototype prototype, NCOrganMedicalStateSnapshot state)
    {
        if (state.StableOrganId == Guid.Empty ||
            !ValidStableOwner(state.DonorKind, state.DonorKey) ||
            state.CyberwareIdentity == null ||
            state.CyberwareIdentity.Length > MaxPersistentPrototypeIdLength ||
            !ComponentShapeMatches<NCOrganIntegrityComponent>(prototype, state.Integrity != null) ||
            !ComponentShapeMatches<NCOrganFunctionComponent>(prototype, state.Function != null) ||
            !ComponentShapeMatches<NCBoneComponent>(prototype, state.Bone != null) ||
            !ComponentShapeMatches<NCTendonComponent>(prototype, state.Tendon != null) ||
            !ComponentShapeMatches<NCJointComponent>(prototype, state.Joint != null) ||
            !ComponentShapeMatches<NCNerveComponent>(prototype, state.Nerve != null) ||
            !ComponentShapeMatches<NCSurgicalSiteComponent>(prototype, state.SurgicalSite != null) ||
            !ComponentShapeMatches<NCCavityFluidComponent>(prototype, state.Cavity != null) ||
            !ComponentShapeMatches<NCCyberwareComponent>(prototype, state.Cyberware != null) ||
            !ValidateTransplantRejection(state) ||
            state.Cyberware is { } cyberware &&
            (!string.Equals(cyberware.EntityPrototype, state.EntityPrototype, StringComparison.Ordinal) ||
             !_cyberware.ValidatePersistentState(cyberware, false)))
        {
            return false;
        }

        if (state.Integrity is { } integrity &&
            (!Finite(integrity.MaxIntegrity, float.Epsilon, 1_000_000f) ||
             !Finite(integrity.CurrentIntegrity, 0f, integrity.MaxIntegrity) ||
             !Finite(integrity.FunctionalEfficiency, 0f, 1f) ||
             !Finite(integrity.Necrosis, 0f, 1f) ||
             !Enum.IsDefined(integrity.Material)))
        {
            return false;
        }

        if (state.Function is { } function &&
            (!Enum.IsDefined(function.FunctionType) ||
             !Finite(function.BaseCapacity, 0f, 1_000_000f) ||
             !Finite(function.CurrentCapacity, 0f, function.BaseCapacity) ||
             !Finite(function.NonFunctionalThreshold, 0f, 1f) ||
             !Finite(function.FailingThreshold, function.NonFunctionalThreshold, 1f) ||
             !Finite(function.ImpairedThreshold, function.FailingThreshold, 1f) ||
             !Finite(function.Necrosis, 0f, 1f) ||
             !Finite(function.PerfusionDemand, 0f, 1_000f) ||
             !Finite(function.OxygenDemand, 0f, 1_000f)))
        {
            return false;
        }

        if (state.Bone is { } bone &&
            (!Enum.IsDefined(bone.State) ||
             !Finite(bone.Stability, 0f, 1f) ||
             !Finite(bone.Displacement, 0f, 1f) ||
             !Finite(bone.SecondaryDamageRisk, 0f, 1f)))
        {
            return false;
        }

        if (state.Tendon is { } tendon &&
            (!Finite(tendon.Integrity, 0f, 1f) ||
             !Finite(tendon.MovementContribution, 0f, 1_000f) ||
             !Finite(tendon.GripContribution, 0f, 1_000f)))
        {
            return false;
        }

        if (state.Joint is { } joint &&
            (!Finite(joint.Stability, 0f, 1f) || !Finite(joint.MovementRestriction, 0f, 1f)))
        {
            return false;
        }

        if (state.Nerve is { } nerve &&
            (!Finite(nerve.NeuralIntegrity, 0f, 1f) ||
             !Finite(nerve.Sensitivity, 0f, 1_000f) ||
             !Finite(nerve.MotorFunction, 0f, 1f)))
        {
            return false;
        }

        if (state.SurgicalSite is { } site &&
            (!Enum.IsDefined(site.AccessDepth) ||
             !Finite(site.Contamination, 0f, 1f) ||
             site.TemporaryDevices.Count > MaxTemporaryDevicesPerSite ||
             site.TemporaryDevices.Any(device =>
                 string.IsNullOrWhiteSpace(device) || device.Length > MaxPersistentStringLength) ||
             site.TemporaryDevices.Distinct(StringComparer.Ordinal).Count() != site.TemporaryDevices.Count ||
             site.Incisions.Count > MaxIncisionsPerSite ||
             site.Incisions.Any(incision => !ValidateIncision(incision)) ||
             site.Incisions.Select(incision => incision.IncisionId).Distinct().Count() != site.Incisions.Count))
        {
            return false;
        }

        if (state.Cavity is { } cavity &&
            (!Enum.IsDefined(cavity.Cavity) ||
             cavity.Cavity == NCBodyCavity.None ||
             !Finite(cavity.MaximumBloodVolume, float.Epsilon, 1_000_000f) ||
             !Finite(cavity.BloodVolume, 0f, cavity.MaximumBloodVolume) ||
             !Finite(cavity.OverflowBloodVolume, 0f, 1_000_000_000f) ||
             !Finite(cavity.PressureFullScaleVolume, float.Epsilon, 1_000_000f) ||
             !Finite(cavity.CompressionFullScaleVolume, float.Epsilon, 1_000_000f) ||
             !Finite(cavity.DrainRate, 0f, 1_000_000f)))
        {
            return false;
        }

        return true;
    }

    private bool ValidateTransplantRejection(NCOrganMedicalStateSnapshot state)
    {
        if (state.Rejection is not { } rejection)
            return true;

        if (!rejection.Initialized ||
            state.Integrity?.Material != NCAnatomicalMaterialKind.Organic ||
            !ValidStableOwner(rejection.DonorKind, rejection.DonorKey) ||
            !ValidStableOwner(rejection.RecipientKind, rejection.RecipientKey) ||
            rejection.DonorKind == NCMedicalStableOwnerKind.Unassigned ||
            rejection.RecipientKind == NCMedicalStableOwnerKind.Unassigned ||
            rejection.DonorKind == rejection.RecipientKind && rejection.DonorKey == rejection.RecipientKey ||
            rejection.DonorKind != state.DonorKind || rejection.DonorKey != state.DonorKey ||
            !Enum.IsDefined(rejection.Stage) ||
            !_prototypes.TryIndex(
                rejection.Profile,
                out NCTransplantRejectionProfilePrototype? profile) ||
            !Finite(rejection.Burden, 0f, profile.MaximumBurden) ||
            profile.StageThresholds.Count != 4)
        {
            return false;
        }

        var expected = rejection.Burden < profile.StageThresholds[0]
            ? NCTransplantRejectionStage.None
            : rejection.Burden < profile.StageThresholds[1]
                ? NCTransplantRejectionStage.Early
                : rejection.Burden < profile.StageThresholds[2]
                    ? NCTransplantRejectionStage.Active
                    : rejection.Burden < profile.StageThresholds[3]
                        ? NCTransplantRejectionStage.Severe
                        : NCTransplantRejectionStage.Critical;
        return rejection.Stage == expected;
    }

    private bool ComponentShapeMatches<T>(EntityPrototype prototype, bool snapshotHasComponent)
        where T : Component, new()
    {
        return prototype.TryComp<T>(out _, _componentFactory) == snapshotHasComponent;
    }

    private bool ValidateWounds(
        NCMedicalStateSnapshot snapshot,
        InitialBodyComponent initial,
        IReadOnlySet<ProtoId<OrganCategoryPrototype>> missing)
    {
        if ((snapshot.Wounds == null) != (snapshot.Bleeding == null))
            return false;
        if (snapshot.Wounds is not { } wounds)
            return snapshot.Treatment == null;

        if (!_prototypes.TryIndex(wounds.Profile, out NCWoundSystemProfilePrototype? profile) ||
            wounds.Records.Count > Math.Min(MaxWoundRecords, Math.Max(0, profile.MaxActiveWounds)) ||
            !_prototypes.TryIndex(profile.ProgressionProfile, out NCWoundProgressionProfilePrototype? progression) ||
            wounds.Scars.Count > Math.Min(MaxScarRecords, Math.Max(0, progression.MaximumScars)) ||
            wounds.OverflowTraumaByZone.Count > MaxOverflowZones ||
            !Finite(wounds.OverflowPain, 0f, 1_000_000_000f))
        {
            return false;
        }

        var initialOrgans = initial.Organs;
        foreach (var (id, wound) in wounds.Records)
        {
            if (id == Guid.Empty || wound.WoundId != id ||
                !_prototypes.TryIndex(wound.Prototype, out NCWoundPrototype? woundPrototype) ||
                !_prototypes.TryIndex(wound.TargetZone, out NCTargetZonePrototype? zone) ||
                !_prototypes.HasIndex(wound.RegionCategory) ||
                !initialOrgans.ContainsKey(wound.RegionCategory) ||
                missing.Contains(wound.RegionCategory) ||
                zone.MedicalRegionCategory != wound.RegionCategory ||
                woundPrototype.WoundType != wound.WoundType ||
                woundPrototype.TissueDepth != wound.TissueDepth ||
                wound.PrimaryDamageType is { } damageType && !_prototypes.HasIndex<DamageTypePrototype>(damageType) ||
                !Enum.IsDefined(wound.WoundType) ||
                !Enum.IsDefined(wound.TissueDepth) ||
                !Enum.IsDefined(wound.Severity) ||
                !Enum.IsDefined(wound.Stage) ||
                !Enum.IsDefined(wound.ClosureState) ||
                !Enum.IsDefined(wound.PrimaryBleedKind) ||
                !Enum.IsDefined(wound.InfectionStage) ||
                !Enum.IsDefined(wound.Surface) ||
                !ValidTreatmentFlags(wound.TreatmentFlags) ||
                !Finite(wound.IntegrityDamage.Float(), 0f, 1_000_000_000f) ||
                !Finite(wound.Area, 0f, 1_000_000_000f) ||
                !Finite(wound.ExternalBleedRate, 0f, 1_000_000f) ||
                !Finite(wound.InternalBleedRate, 0f, 1_000_000f) ||
                !Finite(wound.InitialExternalBleedRate, wound.ExternalBleedRate, 1_000_000f) ||
                !Finite(wound.InitialInternalBleedRate, wound.InternalBleedRate, 1_000_000f) ||
                !Finite(wound.Pain, 0f, 1_000_000_000f) ||
                !Finite(wound.InitialPain, wound.Pain, 1_000_000_000f) ||
                !Finite(wound.Contamination, 0f, 1f) ||
                !Finite(wound.Infection, 0f, 1f) ||
                !Finite(wound.Necrosis, 0f, 1f) ||
                !Finite(wound.HealingProgress, 0f, 1f) ||
                !Finite(wound.PeakHealingProgress, wound.HealingProgress, 1f) ||
                !Finite(wound.ReopenStress, 0f, 1_000_000f) ||
                !Finite(wound.ScarRisk, 0f, 1f) ||
                wound.EmbeddedObjectId == null &&
                    (wound.EmbeddedObjectEntityPrototype != null || wound.SourceProjectilePrototype != null) ||
                wound.EmbeddedObjectEntityPrototype is { } extractedPrototype &&
                    !_prototypes.HasIndex<EntityPrototype>(extractedPrototype) ||
                wound.SourceProjectilePrototype is { } sourceProjectile &&
                    !_prototypes.HasIndex<EntityPrototype>(sourceProjectile) ||
                !ValidTime(wound.Age) ||
                !ValidAbsoluteTime(wound.CreatedAt) ||
                !ValidAbsoluteTime(wound.ModifiedAt) ||
                wound.ModifiedAt < wound.CreatedAt ||
                wound.ExitWoundId == id)
            {
                return false;
            }
        }

        var scarIds = new HashSet<Guid>();
        var sourceWoundIds = new HashSet<Guid>();
        foreach (var scar in wounds.Scars)
        {
            if (scar.ScarId == Guid.Empty || scar.SourceWoundId == Guid.Empty ||
                !scarIds.Add(scar.ScarId) || !sourceWoundIds.Add(scar.SourceWoundId) ||
                !_prototypes.TryIndex(scar.TargetZone, out NCTargetZonePrototype? zone) ||
                !_prototypes.HasIndex(scar.RegionCategory) ||
                !initialOrgans.ContainsKey(scar.RegionCategory) ||
                missing.Contains(scar.RegionCategory) ||
                zone.MedicalRegionCategory != scar.RegionCategory ||
                !Enum.IsDefined(scar.WoundType) ||
                !Enum.IsDefined(scar.OriginalSeverity) ||
                !Enum.IsDefined(scar.Surface) ||
                !Finite(scar.Area, 0f, 1_000_000_000f) ||
                !Finite(scar.Disfigurement, 0f, 1f) ||
                !Finite(scar.Contracture, 0f, 1f) ||
                !ValidAbsoluteTime(scar.CreatedAt))
            {
                return false;
            }
        }

        foreach (var (zoneId, trauma) in wounds.OverflowTraumaByZone)
        {
            if (!_prototypes.TryIndex(zoneId, out NCTargetZonePrototype? zone) ||
                missing.Contains(zone.MedicalRegionCategory) ||
                !Finite(trauma, 0f, 1_000_000_000f))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateBleeding(
        NCMedicalStateSnapshot snapshot,
        InitialBodyComponent initial,
        IReadOnlySet<ProtoId<OrganCategoryPrototype>> missing)
    {
        if (snapshot.Bleeding is not { } bleeding)
            return snapshot.Wounds == null;
        if (snapshot.Wounds is not { } wounds ||
            bleeding.Sources.Count > MaxBleedingSources ||
            bleeding.OverflowInternalRatesByRegion.Count > MaxOrganRecords ||
            !Finite(bleeding.OverflowExternalRate, 0f, 1_000_000f) ||
            !Finite(bleeding.OverflowInternalRate, 0f, 1_000_000f) ||
            !Finite(bleeding.InternalBloodLoss, 0f, 1_000_000_000f) ||
            !Finite(bleeding.UnlocalizedInternalBloodVolume, 0f, 1_000_000_000f))
        {
            return false;
        }


        var attributedInternalOverflow = 0f;
        var initialOrgans = initial.Organs;
        foreach (var (region, rate) in bleeding.OverflowInternalRatesByRegion)
        {
            if (!initialOrgans.ContainsKey(region) || missing.Contains(region) ||
                !Finite(rate, 0f, 1_000_000f))
            {
                return false;
            }

            attributedInternalOverflow += rate;
        }
        if (!float.IsFinite(attributedInternalOverflow) ||
            attributedInternalOverflow > bleeding.OverflowInternalRate + 0.001f)
        {
            return false;
        }

        var sourceIds = new HashSet<Guid>();
        foreach (var source in bleeding.Sources)
        {
            if (source.SourceId == Guid.Empty ||
                !sourceIds.Add(source.SourceId) ||
                !wounds.Records.TryGetValue(source.WoundId, out var wound) ||
                source.TargetZone != wound.TargetZone ||
                source.RegionCategory != wound.RegionCategory ||
                !Enum.IsDefined(source.Channel) ||
                !Finite(source.BaseRate, 0f, 1_000_000f) ||
                !Finite(source.CurrentRate, 0f, 1_000_000f))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateTreatment(NCMedicalStateSnapshot snapshot)
    {
        if (snapshot.Treatment is not { } treatment)
            return true;
        if (snapshot.Wounds is not { } wounds ||
            !_prototypes.HasIndex(treatment.Profile) ||
            treatment.WoundTreatments.Count > MaxWoundRecords ||
            treatment.Tourniquets.Count > MaxTourniquets)
        {
            return false;
        }

        foreach (var (id, record) in treatment.WoundTreatments)
        {
            if (id == Guid.Empty || record.WoundId != id || !wounds.Records.ContainsKey(id) ||
                !ValidTreatmentFlags(record.AppliedFlags) ||
                !ValidOptionalAbsoluteTime(record.PressureAppliedAt) ||
                !ValidOptionalAbsoluteTime(record.BandagedAt) ||
                !ValidOptionalAbsoluteTime(record.HemostaticAppliedAt) ||
                !ValidAbsoluteTime(record.ModifiedAt) ||
                !Finite(record.BandageAbsorptionRemaining, 0f, 1_000_000f) ||
                !Finite(record.BandageSterility, 0f, 1f))
            {
                return false;
            }
        }

        foreach (var (zone, tourniquet) in treatment.Tourniquets)
        {
            if (zone != tourniquet.RootZone || !_prototypes.HasIndex(zone) ||
                tourniquet.CoveredWoundIds.Count > MaxWoundRecords ||
                tourniquet.CoveredWoundIds.Distinct().Count() != tourniquet.CoveredWoundIds.Count ||
                tourniquet.CoveredWoundIds.Any(id => !wounds.Records.ContainsKey(id)) ||
                !ValidAbsoluteTime(tourniquet.AppliedAt) ||
                !ValidAbsoluteTime(tourniquet.IschemiaRiskAt) ||
                !ValidAbsoluteTime(tourniquet.CriticalIschemiaAt) ||
                !ValidOptionalAbsoluteTime(tourniquet.RemovedAt) ||
                !ValidOptionalAbsoluteTime(tourniquet.ReboundEndsAt))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateCirculation(NCCirculationStateSnapshot circulation)
    {
        if (!circulation.Initialized ||
            !_prototypes.TryIndex(circulation.Profile, out NCPhysiologyProfilePrototype? profile) ||
            !Finite(circulation.BloodVolume, 0f, profile.NominalBloodVolume) ||
            !Finite(circulation.EffectiveCirculatingVolume, 0f, profile.NominalBloodVolume) ||
            !Finite(circulation.CardiacOutput, 0f, 1f) ||
            !Finite(circulation.Perfusion, 0f, 1f) ||
            !Enum.IsDefined(circulation.BloodPressureState) ||
            !Enum.IsDefined(circulation.HeartRhythm) ||
            circulation.TransfusionCompatibility.Length > MaxPersistentStringLength)
        {
            return false;
        }

        return true;
    }

    private bool ValidateEmergencyCare(NCEmergencyCareStateSnapshot? emergency)
    {
        if (emergency == null)
            return true;

        return _prototypes.TryIndex(emergency.Profile, out NCEmergencyCareProfilePrototype? profile) &&
               ValidTime(emergency.CprRemaining) &&
               ValidTime(emergency.OxygenRemaining) &&
               ValidTime(emergency.VentilationRemaining) &&
               Finite(emergency.CrystalloidVolume, 0f, profile.MaximumCrystalloidVolume);
    }

    private bool ValidateSystemic(NCSystemicStateSnapshot? systemic)
    {
        if (systemic == null)
            return true;

        var channels = Enum.GetValues<NCSystemicChannel>();
        if (!systemic.Initialized ||
            !_prototypes.TryIndex(systemic.Profile, out NCSystemicPhysiologyProfilePrototype? profile) ||
            systemic.Burdens.Count != channels.Length ||
            channels.Any(channel => !systemic.Burdens.ContainsKey(channel) ||
                                    !profile.Channels.TryGetValue(channel, out var settings) ||
                                    !Finite(systemic.Burdens[channel], 0f, settings.MaximumBurden)))
        {
            return false;
        }

        return true;
    }

    private static bool ValidatePhysiology(NCPhysiologyStateSnapshot physiology)
    {
        return Finite(physiology.Oxygenation, 0f, 1f) &&
               Finite(physiology.TotalPain, 0f, 1_000_000_000f) &&
               Enum.IsDefined(physiology.PainLevel) &&
               Finite(physiology.Shock, 0f, 1f) &&
               Enum.IsDefined(physiology.ShockStage) &&
               Enum.IsDefined(physiology.Consciousness) &&
               Enum.IsDefined(physiology.Condition) &&
               Finite(physiology.BrainFunction, 0f, 1f) &&
               ValidTime(physiology.TimeBelowCriticalPerfusion);
    }

    private static bool ValidTreatmentFlags(NCWoundTreatmentFlags flags)
    {
        const NCWoundTreatmentFlags all =
            NCWoundTreatmentFlags.Pressure |
            NCWoundTreatmentFlags.Bandaged |
            NCWoundTreatmentFlags.Hemostatic |
            NCWoundTreatmentFlags.Clamped |
            NCWoundTreatmentFlags.Tourniqueted |
            NCWoundTreatmentFlags.Cauterized |
            NCWoundTreatmentFlags.Debrided |
            NCWoundTreatmentFlags.Drained;
        return (flags & ~all) == 0;
    }

    private static bool ValidStableOwner(NCMedicalStableOwnerKind kind, string? key)
    {
        if (!Enum.IsDefined(kind) || key == null || key.Length > MaxStableOwnerKeyLength)
            return false;
        return kind switch
        {
            NCMedicalStableOwnerKind.Unassigned => key.Length == 0,
            NCMedicalStableOwnerKind.PlayerCharacter =>
                int.TryParse(key, out var profileId) && profileId > 0,
            NCMedicalStableOwnerKind.PersistentActor =>
                Guid.TryParse(key, out var actorId) && actorId != Guid.Empty,
            _ => false,
        };
    }

    private static bool Finite(float value, float minimum, float maximum)
    {
        return float.IsFinite(value) && value >= minimum && value <= maximum;
    }

    private static bool ValidTime(TimeSpan value)
    {
        return value >= TimeSpan.Zero && value <= TimeSpan.FromDays(3_650_000);
    }

    private static bool ValidAbsoluteTime(TimeSpan value)
    {
        return value >= TimeSpan.FromDays(-3_650_000) && value <= TimeSpan.FromDays(3_650_000);
    }

    private static bool ValidOptionalAbsoluteTime(TimeSpan value)
    {
        return value == TimeSpan.Zero || ValidAbsoluteTime(value);
    }

    private static NCMedicalStateSnapshot CloneSnapshot(NCMedicalStateSnapshot source)
    {
        var clone = new NCMedicalStateSnapshot
        {
            Version = source.Version,
            CapturedAt = source.CapturedAt,
            AnatomyProfile = source.AnatomyProfile,
            MissingOrganCategories = [.. source.MissingOrganCategories],
            CyberwareModules = source.CyberwareModules.Select(CloneCyberware).ToList(),
            Wounds = CloneWounds(source.Wounds),
            Bleeding = CloneBleeding(source.Bleeding),
            Treatment = CloneTreatment(source.Treatment),
            EmergencyCare = source.EmergencyCare is { } emergency
                ? new NCEmergencyCareStateSnapshot
                {
                    Profile = emergency.Profile,
                    CprRemaining = emergency.CprRemaining,
                    OxygenRemaining = emergency.OxygenRemaining,
                    VentilationRemaining = emergency.VentilationRemaining,
                    CrystalloidVolume = emergency.CrystalloidVolume,
                }
                : null,
            Systemic = source.Systemic is { } systemic
                ? new NCSystemicStateSnapshot
                {
                    Profile = systemic.Profile,
                    Initialized = systemic.Initialized,
                    Burdens = new Dictionary<NCSystemicChannel, float>(systemic.Burdens),
                    ShockableArrestLatched = systemic.ShockableArrestLatched,
                }
                : null,
            Pharmacology = ClonePharmacology(source.Pharmacology),
            Circulation = new NCCirculationStateSnapshot
            {
                Profile = source.Circulation.Profile,
                Initialized = source.Circulation.Initialized,
                BloodVolume = source.Circulation.BloodVolume,
                EffectiveCirculatingVolume = source.Circulation.EffectiveCirculatingVolume,
                CardiacOutput = source.Circulation.CardiacOutput,
                Perfusion = source.Circulation.Perfusion,
                BloodPressureState = source.Circulation.BloodPressureState,
                HeartRhythm = source.Circulation.HeartRhythm,
                TransfusionCompatibility = source.Circulation.TransfusionCompatibility,
            },
            Physiology = new NCPhysiologyStateSnapshot
            {
                Oxygenation = source.Physiology.Oxygenation,
                TotalPain = source.Physiology.TotalPain,
                PainLevel = source.Physiology.PainLevel,
                Shock = source.Physiology.Shock,
                ShockStage = source.Physiology.ShockStage,
                Consciousness = source.Physiology.Consciousness,
                Condition = source.Physiology.Condition,
                BrainFunction = source.Physiology.BrainFunction,
                TimeBelowCriticalPerfusion = source.Physiology.TimeBelowCriticalPerfusion,
                Irreversible = source.Physiology.Irreversible,
            },
        };

        foreach (var (category, organ) in source.Organs)
            clone.Organs.Add(category, CloneOrgan(organ));
        return clone;
    }

    private static NCPharmacologyStateSnapshot? ClonePharmacology(NCPharmacologyStateSnapshot? source)
    {
        if (source == null)
            return null;

        return new NCPharmacologyStateSnapshot
        {
            Profile = source.Profile,
            Initialized = source.Initialized,
            OverdoseBurden = source.OverdoseBurden,
            ActiveDrugs = source.ActiveDrugs.Select(exposure => new NCActiveDrugExposureSnapshot
            {
                Profile = exposure.Profile,
                Concentration = exposure.Concentration,
                CumulativeDose = exposure.CumulativeDose,
                TimeSinceAdministration = exposure.TimeSinceAdministration,
            }).ToList(),
        };
    }

    private static NCOrganMedicalStateSnapshot CloneOrgan(NCOrganMedicalStateSnapshot source)
    {
        return new NCOrganMedicalStateSnapshot
        {
            EntityPrototype = source.EntityPrototype,
            StableOrganId = source.StableOrganId,
            DonorKind = source.DonorKind,
            DonorKey = source.DonorKey,
            CyberwareIdentity = source.CyberwareIdentity,
            Integrity = source.Integrity is { } integrity
                ? new NCOrganIntegrityStateSnapshot
                {
                    MaxIntegrity = integrity.MaxIntegrity,
                    CurrentIntegrity = integrity.CurrentIntegrity,
                    FunctionalEfficiency = integrity.FunctionalEfficiency,
                    Recoverable = integrity.Recoverable,
                    Necrosis = integrity.Necrosis,
                    Material = integrity.Material,
                }
                : null,
            Function = source.Function is { } function
                ? new NCOrganFunctionStateSnapshot
                {
                    FunctionType = function.FunctionType,
                    BaseCapacity = function.BaseCapacity,
                    CurrentCapacity = function.CurrentCapacity,
                    ImpairedThreshold = function.ImpairedThreshold,
                    FailingThreshold = function.FailingThreshold,
                    NonFunctionalThreshold = function.NonFunctionalThreshold,
                    Vital = function.Vital,
                    Recoverable = function.Recoverable,
                    Necrosis = function.Necrosis,
                    PerfusionDemand = function.PerfusionDemand,
                    OxygenDemand = function.OxygenDemand,
                }
                : null,
            Bone = source.Bone is { } bone
                ? new NCBoneStateSnapshot
                {
                    State = bone.State,
                    Stability = bone.Stability,
                    Displacement = bone.Displacement,
                    OpenFracture = bone.OpenFracture,
                    SecondaryDamageRisk = bone.SecondaryDamageRisk,
                    RequiresFixation = bone.RequiresFixation,
                    Splinted = bone.Splinted,
                    SplintedAt = bone.SplintedAt,
                }
                : null,
            Tendon = source.Tendon is { } tendon
                ? new NCTendonStateSnapshot
                {
                    Integrity = tendon.Integrity,
                    Ruptured = tendon.Ruptured,
                    MovementContribution = tendon.MovementContribution,
                    GripContribution = tendon.GripContribution,
                    SurgicallyRepairable = tendon.SurgicallyRepairable,
                }
                : null,
            Joint = source.Joint is { } joint
                ? new NCJointStateSnapshot
                {
                    Stability = joint.Stability,
                    Dislocated = joint.Dislocated,
                    MovementRestriction = joint.MovementRestriction,
                    Reduced = joint.Reduced,
                }
                : null,
            Nerve = source.Nerve is { } nerve
                ? new NCNerveStateSnapshot
                {
                    NeuralIntegrity = nerve.NeuralIntegrity,
                    Sensitivity = nerve.Sensitivity,
                    MotorFunction = nerve.MotorFunction,
                    CyberprostheticInterface = nerve.CyberprostheticInterface,
                }
                : null,
            SurgicalSite = source.SurgicalSite is { } site
                ? new NCSurgicalSiteStateSnapshot
                {
                    AccessDepth = site.AccessDepth,
                    BleedingControlled = site.BleedingControlled,
                    BoneCasingOpen = site.BoneCasingOpen,
                    TissuesRetracted = site.TissuesRetracted,
                    Contamination = site.Contamination,
                    TemporaryDevices = [.. site.TemporaryDevices],
                    Incisions = site.Incisions.Select(CloneIncision).ToList(),
                }
                : null,
            Cavity = source.Cavity is { } cavity
                ? new NCCavityFluidStateSnapshot
                {
                    Cavity = cavity.Cavity,
                    BloodVolume = cavity.BloodVolume,
                    OverflowBloodVolume = cavity.OverflowBloodVolume,
                    MaximumBloodVolume = cavity.MaximumBloodVolume,
                    PressureFullScaleVolume = cavity.PressureFullScaleVolume,
                    CompressionFullScaleVolume = cavity.CompressionFullScaleVolume,
                    DrainRate = cavity.DrainRate,
                }
                : null,
            Cyberware = source.Cyberware is { } cyberware
                ? CloneCyberware(cyberware)
                : null,
            Rejection = source.Rejection is { } rejection
                ? new NCTransplantRejectionStateSnapshot
                {
                    Profile = rejection.Profile,
                    Initialized = rejection.Initialized,
                    DonorKind = rejection.DonorKind,
                    DonorKey = rejection.DonorKey,
                    RecipientKind = rejection.RecipientKind,
                    RecipientKey = rejection.RecipientKey,
                    Burden = rejection.Burden,
                    Stage = rejection.Stage,
                }
                : null,
        };
    }

    private static NCCyberwareStateSnapshot CloneCyberware(NCCyberwareStateSnapshot source)
    {
        return new NCCyberwareStateSnapshot
        {
            EntityPrototype = source.EntityPrototype,
            Profile = source.Profile,
            StableInstanceId = source.StableInstanceId,
            Slot = source.Slot,
            HostCategory = source.HostCategory,
            CurrentIntegrity = source.CurrentIntegrity,
            MaximumIntegrity = source.MaximumIntegrity,
            CurrentPower = source.CurrentPower,
            Heat = source.Heat,
            Load = source.Load,
            EmpDisabledUntil = source.EmpDisabledUntil,
            NeuralIntegrity = source.NeuralIntegrity,
            NeuralState = source.NeuralState,
            Calibrated = source.Calibrated,
        };
    }

    private static NCWoundStateSnapshot? CloneWounds(NCWoundStateSnapshot? source)
    {
        if (source == null)
            return null;
        var clone = new NCWoundStateSnapshot
        {
            Profile = source.Profile,
            Scars = source.Scars.Select(scar => new NCScarRecordSnapshot
            {
                ScarId = scar.ScarId,
                SourceWoundId = scar.SourceWoundId,
                TargetZone = scar.TargetZone,
                RegionCategory = scar.RegionCategory,
                WoundType = scar.WoundType,
                OriginalSeverity = scar.OriginalSeverity,
                Area = scar.Area,
                Disfigurement = scar.Disfigurement,
                Contracture = scar.Contracture,
                VisualX = scar.VisualX,
                VisualY = scar.VisualY,
                VisualDirection = scar.VisualDirection,
                Surface = scar.Surface,
                CreatedAt = scar.CreatedAt,
            }).ToList(),
            OverflowTraumaByZone = new Dictionary<ProtoId<NCTargetZonePrototype>, float>(source.OverflowTraumaByZone),
            OverflowPain = source.OverflowPain,
        };
        foreach (var (id, wound) in source.Records)
        {
            clone.Records.Add(id, new NCWoundRecordSnapshot
            {
                WoundId = wound.WoundId,
                Prototype = wound.Prototype,
                TargetZone = wound.TargetZone,
                RegionCategory = wound.RegionCategory,
                PrimaryDamageType = wound.PrimaryDamageType,
                WoundType = wound.WoundType,
                TissueDepth = wound.TissueDepth,
                Severity = wound.Severity,
                Stage = wound.Stage,
                ClosureState = wound.ClosureState,
                TreatmentFlags = wound.TreatmentFlags,
                IntegrityDamage = wound.IntegrityDamage,
                Area = wound.Area,
                ExternalBleedRate = wound.ExternalBleedRate,
                InternalBleedRate = wound.InternalBleedRate,
                InitialExternalBleedRate = wound.InitialExternalBleedRate,
                InitialInternalBleedRate = wound.InitialInternalBleedRate,
                PrimaryBleedKind = wound.PrimaryBleedKind,
                Pain = wound.Pain,
                InitialPain = wound.InitialPain,
                Contamination = wound.Contamination,
                Infection = wound.Infection,
                InfectionStage = wound.InfectionStage,
                Necrosis = wound.Necrosis,
                HealingProgress = wound.HealingProgress,
                PeakHealingProgress = wound.PeakHealingProgress,
                ReopenStress = wound.ReopenStress,
                ScarRisk = wound.ScarRisk,
                EmbeddedObjectId = wound.EmbeddedObjectId,
                EmbeddedObjectEntityPrototype = wound.EmbeddedObjectEntityPrototype,
                SourceProjectilePrototype = wound.SourceProjectilePrototype,
                ExitWoundId = wound.ExitWoundId,
                // Keep the migration field empty when cloning a validated persistence payload.
                ForensicResolutionId = Guid.Empty,
                VisualX = wound.VisualX,
                VisualY = wound.VisualY,
                VisualDirection = wound.VisualDirection,
                Surface = wound.Surface,
                CreatedAt = wound.CreatedAt,
                ModifiedAt = wound.ModifiedAt,
                Age = wound.Age,
            });
        }
        return clone;
    }

    private static NCBleedingStateSnapshot? CloneBleeding(NCBleedingStateSnapshot? source)
    {
        if (source == null)
            return null;
        return new NCBleedingStateSnapshot
        {
            Sources = source.Sources.Select(bleeding => new NCBleedingSourceStateSnapshot
            {
                SourceId = bleeding.SourceId,
                WoundId = bleeding.WoundId,
                TargetZone = bleeding.TargetZone,
                RegionCategory = bleeding.RegionCategory,
                Channel = bleeding.Channel,
                BaseRate = bleeding.BaseRate,
                CurrentRate = bleeding.CurrentRate,
                PressureApplied = bleeding.PressureApplied,
                Bandaged = bleeding.Bandaged,
                Clamped = bleeding.Clamped,
                Tourniqueted = bleeding.Tourniqueted,
                Cauterized = bleeding.Cauterized,
            }).ToList(),
            OverflowExternalRate = source.OverflowExternalRate,
            OverflowInternalRate = source.OverflowInternalRate,
            OverflowInternalRatesByRegion = new Dictionary<ProtoId<OrganCategoryPrototype>, float>(
                source.OverflowInternalRatesByRegion),
            InternalBloodLoss = source.InternalBloodLoss,
            UnlocalizedInternalBloodVolume = source.UnlocalizedInternalBloodVolume,
        };
    }

    private static NCTreatmentStateSnapshot? CloneTreatment(NCTreatmentStateSnapshot? source)
    {
        if (source == null)
            return null;
        var clone = new NCTreatmentStateSnapshot { Profile = source.Profile };
        foreach (var (id, treatment) in source.WoundTreatments)
        {
            clone.WoundTreatments.Add(id, new NCWoundTreatmentStateSnapshot
            {
                WoundId = treatment.WoundId,
                AppliedFlags = treatment.AppliedFlags,
                PressureAppliedAt = treatment.PressureAppliedAt,
                BandagedAt = treatment.BandagedAt,
                HemostaticAppliedAt = treatment.HemostaticAppliedAt,
                BandageAbsorptionRemaining = treatment.BandageAbsorptionRemaining,
                BandageSterility = treatment.BandageSterility,
                ModifiedAt = treatment.ModifiedAt,
            });
        }
        foreach (var (zone, tourniquet) in source.Tourniquets)
        {
            clone.Tourniquets.Add(zone, new NCTourniquetStateSnapshot
            {
                RootZone = tourniquet.RootZone,
                CoveredWoundIds = [.. tourniquet.CoveredWoundIds],
                Active = tourniquet.Active,
                AppliedAt = tourniquet.AppliedAt,
                IschemiaRiskAt = tourniquet.IschemiaRiskAt,
                CriticalIschemiaAt = tourniquet.CriticalIschemiaAt,
                RemovedAt = tourniquet.RemovedAt,
                ReboundEndsAt = tourniquet.ReboundEndsAt,
                ControlledRelease = tourniquet.ControlledRelease,
            });
        }
        return clone;
    }
}
