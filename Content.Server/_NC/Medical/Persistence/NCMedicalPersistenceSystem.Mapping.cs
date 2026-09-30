// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    private static NCSurgicalIncisionStateSnapshot CaptureIncision(NCSurgicalIncisionRecord incision)
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

    private static NCSurgicalIncisionRecord RestoreIncision(NCSurgicalIncisionStateSnapshot incision)
    {
        return new NCSurgicalIncisionRecord
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

    private bool TryCaptureOrgan(
        EntityUid character,
        EntityUid organ,
        bool allowIdentityMutation,
        out NCOrganMedicalStateSnapshot result)
    {
        result = default!;
        var entityPrototype = MetaData(organ).EntityPrototype?.ID;
        var canMutateIdentity = allowIdentityMutation && !TerminatingOrDeleted(organ);
        if (!TryComp<NCOrganPersistenceIdentityComponent>(organ, out var identity))
        {
            if (!canMutateIdentity)
                return false;
            identity = EnsureComp<NCOrganPersistenceIdentityComponent>(organ);
        }

        if (identity.StableOrganId == Guid.Empty)
        {
            if (!canMutateIdentity)
                return false;
            identity.StableOrganId = Guid.NewGuid();
        }

        var donorKind = identity.DonorKind;
        var donorKey = identity.DonorKey;
        if (donorKind == NCMedicalStableOwnerKind.Unassigned &&
            TryComp<NCMedicalPersistenceOwnerComponent>(character, out var owner))
        {
            donorKind = owner.Kind;
            donorKey = owner.Key;
            if (canMutateIdentity)
            {
                identity.DonorKind = donorKind;
                identity.DonorKey = donorKey;
            }
        }

        result = new NCOrganMedicalStateSnapshot
        {
            EntityPrototype = entityPrototype ?? string.Empty,
            StableOrganId = identity.StableOrganId,
            DonorKind = donorKind,
            DonorKey = donorKey,
            CyberwareIdentity = identity.CyberwareIdentity,
        };
        if (TryComp<NCOrganIntegrityComponent>(organ, out var integrity))
        {
            result.Integrity = new NCOrganIntegrityStateSnapshot
            {
                MaxIntegrity = integrity.MaxIntegrity,
                CurrentIntegrity = integrity.CurrentIntegrity,
                FunctionalEfficiency = integrity.FunctionalEfficiency,
                Recoverable = integrity.Recoverable,
                Necrosis = integrity.Necrosis,
                Material = integrity.Material,
            };

            if (integrity.Material != NCAnatomicalMaterialKind.Organic &&
                string.IsNullOrWhiteSpace(result.CyberwareIdentity))
            {
                result.CyberwareIdentity = result.EntityPrototype;
                if (canMutateIdentity)
                    identity.CyberwareIdentity = result.CyberwareIdentity;
            }
        }

        if (TryComp<NCOrganFunctionComponent>(organ, out var function))
        {
            result.Function = new NCOrganFunctionStateSnapshot
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
            };
        }

        if (TryComp<NCBoneComponent>(organ, out var bone))
        {
            result.Bone = new NCBoneStateSnapshot
            {
                State = bone.State,
                Stability = bone.Stability,
                Displacement = bone.Displacement,
                OpenFracture = bone.OpenFracture,
                SecondaryDamageRisk = bone.SecondaryDamageRisk,
                RequiresFixation = bone.RequiresFixation,
                Splinted = bone.Splinted,
                SplintedAt = bone.SplintedAt,
            };
        }

        if (TryComp<NCTendonComponent>(organ, out var tendon))
        {
            result.Tendon = new NCTendonStateSnapshot
            {
                Integrity = tendon.Integrity,
                Ruptured = tendon.Ruptured,
                MovementContribution = tendon.MovementContribution,
                GripContribution = tendon.GripContribution,
                SurgicallyRepairable = tendon.SurgicallyRepairable,
            };
        }

        if (TryComp<NCJointComponent>(organ, out var joint))
        {
            result.Joint = new NCJointStateSnapshot
            {
                Stability = joint.Stability,
                Dislocated = joint.Dislocated,
                MovementRestriction = joint.MovementRestriction,
                Reduced = joint.Reduced,
            };
        }

        if (TryComp<NCNerveComponent>(organ, out var nerve))
        {
            result.Nerve = new NCNerveStateSnapshot
            {
                NeuralIntegrity = nerve.NeuralIntegrity,
                Sensitivity = nerve.Sensitivity,
                MotorFunction = nerve.MotorFunction,
                CyberprostheticInterface = nerve.CyberprostheticInterface,
            };
        }

        if (TryComp<NCSurgicalSiteComponent>(organ, out var surgicalSite))
        {
            result.SurgicalSite = new NCSurgicalSiteStateSnapshot
            {
                AccessDepth = surgicalSite.AccessDepth,
                BleedingControlled = surgicalSite.BleedingControlled,
                BoneCasingOpen = surgicalSite.BoneCasingOpen,
                TissuesRetracted = surgicalSite.TissuesRetracted,
                Contamination = surgicalSite.Contamination,
                // Temporary instruments are physical round entities and are never reconstructed from a
                // character snapshot. Only the incision and its clinical aftermath are persistent.
                TemporaryDevices = [],
                Incisions = surgicalSite.Incisions
                    .OrderBy(incision => incision.CreatedAt)
                    .ThenBy(incision => incision.IncisionId)
                    .Select(CaptureIncision)
                    .ToList(),
            };
        }

        if (TryComp<NCCavityFluidComponent>(organ, out var cavity))
        {
            result.Cavity = new NCCavityFluidStateSnapshot
            {
                Cavity = cavity.Cavity,
                BloodVolume = cavity.BloodVolume,
                OverflowBloodVolume = cavity.OverflowBloodVolume,
                MaximumBloodVolume = cavity.MaximumBloodVolume,
                PressureFullScaleVolume = cavity.PressureFullScaleVolume,
                CompressionFullScaleVolume = cavity.CompressionFullScaleVolume,
                // A drain is a physical runtime attachment, not a character injury. Retained blood persists;
                // the device's active pumping state does not cross rounds.
                DrainRate = 0f,
            };
        }

        if (HasComp<NCCyberwareComponent>(organ) &&
            _cyberware.TryCaptureOrganState(organ, out var cyberware))
        {
            result.Cyberware = cyberware;
        }

        if (TryComp<NCTransplantRejectionComponent>(organ, out var rejection))
        {
            result.Rejection = new NCTransplantRejectionStateSnapshot
            {
                Profile = rejection.Profile,
                Initialized = rejection.StateInitialized,
                DonorKind = rejection.DonorKind,
                DonorKey = rejection.DonorKey,
                RecipientKind = rejection.RecipientKind,
                RecipientKey = rejection.RecipientKey,
                Burden = rejection.Burden,
                Stage = rejection.Stage,
            };
        }

        return true;
    }

    private NCWoundStateSnapshot? CaptureWounds(EntityUid character)
    {
        if (!TryComp<NCWoundContainerComponent>(character, out var wounds))
            return null;

        var result = new NCWoundStateSnapshot
        {
            Profile = wounds.Profile,
            OverflowTraumaByZone = new Dictionary<Robust.Shared.Prototypes.ProtoId<Content.Shared._NC.Combat.Targeting.NCTargetZonePrototype>, float>(wounds.OverflowTraumaByZone),
            OverflowPain = wounds.OverflowPain,
        };
        foreach (var (id, wound) in wounds.Wounds)
            result.Records.Add(id, CaptureWound(wound));
        foreach (var scar in wounds.Scars)
            result.Scars.Add(CaptureScar(scar));
        return result;
    }

    private static NCWoundRecordSnapshot CaptureWound(NCWoundRecord wound)
    {
        return new NCWoundRecordSnapshot
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
            // Combat provenance is intentionally round-local and never enters character persistence.
            ForensicResolutionId = Guid.Empty,
            VisualX = wound.VisualX,
            VisualY = wound.VisualY,
            VisualDirection = wound.VisualDirection,
            Surface = wound.Surface,
            CreatedAt = wound.CreatedAt,
            ModifiedAt = wound.ModifiedAt,
            Age = wound.Age,
        };
    }

    private static NCScarRecordSnapshot CaptureScar(NCScarRecord scar)
    {
        return new NCScarRecordSnapshot
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
        };
    }

    private NCBleedingStateSnapshot? CaptureBleeding(EntityUid character)
    {
        if (!TryComp<NCBleedingComponent>(character, out var bleeding))
            return null;

        return new NCBleedingStateSnapshot
        {
            Sources = bleeding.Sources.Select(CaptureBleedingSource).ToList(),
            OverflowExternalRate = bleeding.OverflowExternalRate,
            OverflowInternalRate = bleeding.OverflowInternalRate,
            OverflowInternalRatesByRegion = new Dictionary<Robust.Shared.Prototypes.ProtoId<OrganCategoryPrototype>, float>(
                bleeding.OverflowInternalRatesByRegion),
            InternalBloodLoss = bleeding.InternalBloodLoss,
            UnlocalizedInternalBloodVolume = bleeding.UnlocalizedInternalBloodVolume,
        };
    }

    private static NCBleedingSourceStateSnapshot CaptureBleedingSource(NCBleedingSourceRecord source)
    {
        return new NCBleedingSourceStateSnapshot
        {
            SourceId = source.SourceId,
            WoundId = source.WoundId,
            TargetZone = source.TargetZone,
            RegionCategory = source.RegionCategory,
            Channel = source.Channel,
            BaseRate = source.BaseRate,
            CurrentRate = source.CurrentRate,
            PressureApplied = source.PressureApplied,
            Bandaged = source.Bandaged,
            Clamped = source.Clamped,
            Tourniqueted = source.Tourniqueted,
            Cauterized = source.Cauterized,
        };
    }

    private NCTreatmentStateSnapshot? CaptureTreatment(EntityUid character)
    {
        if (!TryComp<NCTreatmentStateComponent>(character, out var treatment))
            return null;

        var result = new NCTreatmentStateSnapshot
        {
            Profile = treatment.Profile,
        };
        foreach (var (id, record) in treatment.WoundTreatments)
            result.WoundTreatments.Add(id, CaptureWoundTreatment(record));
        foreach (var (zone, record) in treatment.Tourniquets)
            result.Tourniquets.Add(zone, CaptureTourniquet(record));
        return result;
    }

    private static NCWoundTreatmentStateSnapshot CaptureWoundTreatment(NCWoundTreatmentRecord record)
    {
        return new NCWoundTreatmentStateSnapshot
        {
            WoundId = record.WoundId,
            AppliedFlags = record.AppliedFlags,
            PressureAppliedAt = record.PressureAppliedAt,
            BandagedAt = record.BandagedAt,
            HemostaticAppliedAt = record.HemostaticAppliedAt,
            BandageAbsorptionRemaining = record.BandageAbsorptionRemaining,
            BandageSterility = record.BandageSterility,
            ModifiedAt = record.ModifiedAt,
        };
    }

    private static NCTourniquetStateSnapshot CaptureTourniquet(NCTourniquetRecord record)
    {
        return new NCTourniquetStateSnapshot
        {
            RootZone = record.RootZone,
            CoveredWoundIds = [.. record.CoveredWoundIds],
            Active = record.Active,
            AppliedAt = record.AppliedAt,
            IschemiaRiskAt = record.IschemiaRiskAt,
            CriticalIschemiaAt = record.CriticalIschemiaAt,
            RemovedAt = record.RemovedAt,
            ReboundEndsAt = record.ReboundEndsAt,
            ControlledRelease = record.ControlledRelease,
        };
    }

    private NCEmergencyCareStateSnapshot? CaptureEmergencyCare(EntityUid character)
    {
        if (!TryComp<NCEmergencyCareStateComponent>(character, out var emergency))
            return null;

        return new NCEmergencyCareStateSnapshot
        {
            Profile = emergency.Profile,
            CprRemaining = emergency.CprRemaining,
            OxygenRemaining = emergency.OxygenRemaining,
            VentilationRemaining = emergency.VentilationRemaining,
            CrystalloidVolume = emergency.CrystalloidVolume,
        };
    }

    private NCSystemicStateSnapshot? CaptureSystemic(EntityUid character)
    {
        if (!TryComp<NCSystemicStateComponent>(character, out var systemic))
            return null;

        return new NCSystemicStateSnapshot
        {
            Profile = systemic.Profile,
            Initialized = systemic.StateInitialized,
            Burdens = new Dictionary<NCSystemicChannel, float>(systemic.Burdens),
            ShockableArrestLatched = systemic.ShockableArrestLatched,
        };
    }

    private NCCirculationStateSnapshot CaptureCirculation(EntityUid character)
    {
        if (!TryComp<NCCirculationComponent>(character, out var circulation))
            return new NCCirculationStateSnapshot();

        return new NCCirculationStateSnapshot
        {
            Profile = circulation.Profile,
            Initialized = circulation.StateInitialized,
            BloodVolume = circulation.BloodVolume,
            EffectiveCirculatingVolume = circulation.EffectiveCirculatingVolume,
            CardiacOutput = circulation.CardiacOutput,
            Perfusion = circulation.Perfusion,
            BloodPressureState = circulation.BloodPressureState,
            HeartRhythm = circulation.HeartRhythm,
            TransfusionCompatibility = circulation.TransfusionCompatibility,
        };
    }

    private NCPhysiologyStateSnapshot CapturePhysiology(EntityUid character)
    {
        if (!TryComp<NCPhysiologyComponent>(character, out var physiology))
            return new NCPhysiologyStateSnapshot();

        return new NCPhysiologyStateSnapshot
        {
            Oxygenation = physiology.Oxygenation,
            TotalPain = physiology.TotalPain,
            PainLevel = physiology.PainLevel,
            Shock = physiology.Shock,
            ShockStage = physiology.ShockStage,
            Consciousness = physiology.Consciousness,
            Condition = physiology.Condition,
            BrainFunction = physiology.BrainFunction,
            TimeBelowCriticalPerfusion = physiology.TimeBelowCriticalPerfusion,
            TimeInClinicalDeath = physiology.TimeInClinicalDeath,
            Irreversible = physiology.Irreversible,
        };
    }

    private bool ApplyOrganState(
        EntityUid character,
        NCMedicalStateSnapshot snapshot,
        IReadOnlyDictionary<Robust.Shared.Prototypes.ProtoId<OrganCategoryPrototype>, EntityUid> organs)
    {
        foreach (var (category, state) in snapshot.Organs)
        {
            var organ = organs[category];
            var identity = EnsureComp<NCOrganPersistenceIdentityComponent>(organ);
            identity.StableOrganId = state.StableOrganId;
            identity.DonorKind = state.DonorKind;
            identity.DonorKey = state.DonorKey;
            identity.CyberwareIdentity = state.CyberwareIdentity;

            if (state.Integrity is { } integrityState && TryComp<NCOrganIntegrityComponent>(organ, out var integrity))
            {
                integrity.MaxIntegrity = integrityState.MaxIntegrity;
                integrity.CurrentIntegrity = integrityState.CurrentIntegrity;
                integrity.FunctionalEfficiency = integrityState.FunctionalEfficiency;
                integrity.Recoverable = integrityState.Recoverable;
                integrity.Necrosis = integrityState.Necrosis;
                integrity.Material = integrityState.Material;
            }

            if (state.Cavity is { } cavityState && TryComp<NCCavityFluidComponent>(organ, out var cavity))
            {
                cavity.Cavity = cavityState.Cavity;
                cavity.BloodVolume = cavityState.BloodVolume;
                cavity.OverflowBloodVolume = cavityState.OverflowBloodVolume;
                cavity.MaximumBloodVolume = cavityState.MaximumBloodVolume;
                cavity.PressureFullScaleVolume = cavityState.PressureFullScaleVolume;
                cavity.CompressionFullScaleVolume = cavityState.CompressionFullScaleVolume;
                cavity.DrainRate = 0f;
                var totalBloodVolume = NCCavityFluidMath.GetTotalBloodVolume(cavity);
                cavity.Pressure = Math.Clamp(totalBloodVolume / cavity.PressureFullScaleVolume, 0f, 1f);
                cavity.OrganCompression = Math.Clamp(
                    totalBloodVolume / cavity.CompressionFullScaleVolume,
                    0f,
                    1f);
            }

            if (state.Function is { } functionState && TryComp<NCOrganFunctionComponent>(organ, out var function))
            {
                function.FunctionType = functionState.FunctionType;
                function.BaseCapacity = functionState.BaseCapacity;
                function.CurrentCapacity = functionState.CurrentCapacity;
                function.ImpairedThreshold = functionState.ImpairedThreshold;
                function.FailingThreshold = functionState.FailingThreshold;
                function.NonFunctionalThreshold = functionState.NonFunctionalThreshold;
                function.Vital = functionState.Vital;
                function.Recoverable = functionState.Recoverable;
                function.Necrosis = functionState.Necrosis;
                function.PerfusionDemand = functionState.PerfusionDemand;
                function.OxygenDemand = functionState.OxygenDemand;
            }

            if (state.Bone is { } boneState && TryComp<NCBoneComponent>(organ, out var bone))
            {
                bone.State = boneState.State;
                bone.Stability = boneState.Stability;
                bone.Displacement = boneState.Displacement;
                bone.OpenFracture = boneState.OpenFracture;
                bone.SecondaryDamageRisk = boneState.SecondaryDamageRisk;
                bone.RequiresFixation = boneState.RequiresFixation;
                bone.Splinted = boneState.Splinted;
                bone.SplintedAt = boneState.SplintedAt;
            }

            if (state.Tendon is { } tendonState && TryComp<NCTendonComponent>(organ, out var tendon))
            {
                tendon.Integrity = tendonState.Integrity;
                tendon.Ruptured = tendonState.Ruptured;
                tendon.MovementContribution = tendonState.MovementContribution;
                tendon.GripContribution = tendonState.GripContribution;
                tendon.SurgicallyRepairable = tendonState.SurgicallyRepairable;
            }

            if (state.Joint is { } jointState && TryComp<NCJointComponent>(organ, out var joint))
            {
                joint.Stability = jointState.Stability;
                joint.Dislocated = jointState.Dislocated;
                joint.MovementRestriction = jointState.MovementRestriction;
                joint.Reduced = jointState.Reduced;
            }

            if (state.Nerve is { } nerveState && TryComp<NCNerveComponent>(organ, out var nerve))
            {
                nerve.NeuralIntegrity = nerveState.NeuralIntegrity;
                nerve.Sensitivity = nerveState.Sensitivity;
                nerve.MotorFunction = nerveState.MotorFunction;
                nerve.CyberprostheticInterface = nerveState.CyberprostheticInterface;
            }

            if (state.SurgicalSite is { } siteState && TryComp<NCSurgicalSiteComponent>(organ, out var site))
            {
                site.AccessDepth = siteState.AccessDepth;
                site.BleedingControlled = siteState.BleedingControlled;
                site.BoneCasingOpen = siteState.BoneCasingOpen;
                site.TissuesRetracted = siteState.TissuesRetracted;
                site.Contamination = siteState.Contamination;
                site.ActiveProcedure = null;
                site.TemporaryDevices.Clear();
                site.Incisions = siteState.Incisions.Select(RestoreIncision).ToList();
                site.SpatialRevision = 0;
            }

            if (state.Cyberware is { } cyberwareState &&
                !_cyberware.TryApplyPersistentState(organ, cyberwareState, _timing.CurTime))
            {
                return false;
            }

            if (state.Rejection is { } rejectionState)
            {
                var rejection = EnsureComp<NCTransplantRejectionComponent>(organ);
                rejection.Profile = rejectionState.Profile;
                rejection.StateInitialized = rejectionState.Initialized;
                rejection.DonorKind = rejectionState.DonorKind;
                rejection.DonorKey = rejectionState.DonorKey;
                rejection.RecipientKind = rejectionState.RecipientKind;
                rejection.RecipientKey = rejectionState.RecipientKey;
                rejection.Burden = rejectionState.Burden;
                rejection.Stage = rejectionState.Stage;
            }
            else
            {
                RemComp<NCTransplantRejectionComponent>(organ);
            }
        }

        return true;
    }

    private void ApplyWoundState(EntityUid character, NCWoundStateSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            RemComp<NCWoundContainerComponent>(character);
            return;
        }

        var wounds = EnsureComp<NCWoundContainerComponent>(character);
        wounds.Profile = snapshot.Profile;
        wounds.Wounds = snapshot.Records.ToDictionary(entry => entry.Key, entry => RestoreWound(entry.Value));
        wounds.Scars = snapshot.Scars.Select(RestoreScar).ToList();
        wounds.OverflowTraumaByZone = new Dictionary<Robust.Shared.Prototypes.ProtoId<Content.Shared._NC.Combat.Targeting.NCTargetZonePrototype>, float>(snapshot.OverflowTraumaByZone);
        wounds.OverflowPain = snapshot.OverflowPain;
        wounds.ProcessedImpacts.Clear();
        wounds.ProcessedImpactOrder.Clear();
        wounds.PendingMovementDistance = 0f;
        wounds.LastProgressionPersistenceEventAt = TimeSpan.Zero;
    }

    private static NCWoundRecord RestoreWound(NCWoundRecordSnapshot wound)
    {
        return new NCWoundRecord
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
            InitialExternalBleedRate = wound.InitialExternalBleedRate > 0f
                ? wound.InitialExternalBleedRate
                : wound.ExternalBleedRate,
            InitialInternalBleedRate = wound.InitialInternalBleedRate > 0f
                ? wound.InitialInternalBleedRate
                : wound.InternalBleedRate,
            PrimaryBleedKind = wound.PrimaryBleedKind,
            Pain = wound.Pain,
            InitialPain = wound.InitialPain > 0f ? wound.InitialPain : wound.Pain,
            Contamination = wound.Contamination,
            Infection = wound.Infection,
            InfectionStage = wound.InfectionStage,
            Necrosis = wound.Necrosis,
            HealingProgress = wound.HealingProgress,
            PeakHealingProgress = MathF.Max(wound.PeakHealingProgress, wound.HealingProgress),
            ReopenStress = wound.ReopenStress,
            ScarRisk = wound.ScarRisk,
            EmbeddedObjectId = wound.EmbeddedObjectId,
            EmbeddedObjectEntityPrototype = wound.EmbeddedObjectEntityPrototype,
            SourceProjectilePrototype = wound.SourceProjectilePrototype,
            ExitWoundId = wound.ExitWoundId,
            // Ignore legacy stored provenance when materializing a new round-local wound record.
            ForensicResolutionId = Guid.Empty,
            VisualX = wound.VisualX,
            VisualY = wound.VisualY,
            VisualDirection = wound.VisualDirection,
            Surface = wound.Surface,
            CreatedAt = wound.CreatedAt,
            ModifiedAt = wound.ModifiedAt,
            Age = wound.Age,
        };
    }

    private static NCScarRecord RestoreScar(NCScarRecordSnapshot scar)
    {
        return new NCScarRecord
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
        };
    }

    private void ApplyBleedingState(EntityUid character, NCBleedingStateSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            RemComp<NCBleedingComponent>(character);
            return;
        }

        var bleeding = EnsureComp<NCBleedingComponent>(character);
        bleeding.Sources = snapshot.Sources.Select(RestoreBleedingSource).ToList();
        bleeding.OverflowExternalRate = snapshot.OverflowExternalRate;
        bleeding.OverflowInternalRate = snapshot.OverflowInternalRate;
        bleeding.OverflowInternalRatesByRegion = new Dictionary<Robust.Shared.Prototypes.ProtoId<OrganCategoryPrototype>, float>(
            snapshot.OverflowInternalRatesByRegion);
        bleeding.InternalBloodLoss = snapshot.InternalBloodLoss;
        bleeding.UnlocalizedInternalBloodVolume = snapshot.UnlocalizedInternalBloodVolume;

        // Derived aggregate rates are rebuilt from the persistent channels, never trusted from storage.
        bleeding.ActiveExternalRate = MathF.Max(0f, bleeding.OverflowExternalRate);
        bleeding.ActiveInternalRate = MathF.Max(0f, bleeding.OverflowInternalRate);
        bleeding.ActiveArterialRate = 0f;
        foreach (var source in bleeding.Sources)
        {
            var rate = MathF.Max(0f, source.CurrentRate);
            if (source.Channel == NCBleedingChannel.Internal)
                bleeding.ActiveInternalRate += rate;
            else
                bleeding.ActiveExternalRate += rate;
            if (source.Channel == NCBleedingChannel.ExternalArterial)
                bleeding.ActiveArterialRate += rate;
        }
    }

    private static NCBleedingSourceRecord RestoreBleedingSource(NCBleedingSourceStateSnapshot source)
    {
        return new NCBleedingSourceRecord
        {
            SourceId = source.SourceId,
            WoundId = source.WoundId,
            TargetZone = source.TargetZone,
            RegionCategory = source.RegionCategory,
            Channel = source.Channel,
            BaseRate = source.BaseRate,
            CurrentRate = source.CurrentRate,
            PressureApplied = source.PressureApplied,
            Bandaged = source.Bandaged,
            Clamped = source.Clamped,
            Tourniqueted = source.Tourniqueted,
            Cauterized = source.Cauterized,
        };
    }

    private void ApplyTreatmentState(EntityUid character, NCTreatmentStateSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            RemComp<NCTreatmentStateComponent>(character);
            return;
        }

        var treatment = EnsureComp<NCTreatmentStateComponent>(character);
        treatment.Profile = snapshot.Profile;
        treatment.WoundTreatments = snapshot.WoundTreatments.ToDictionary(
            entry => entry.Key,
            entry => RestoreWoundTreatment(entry.Value));
        treatment.Tourniquets = snapshot.Tourniquets.ToDictionary(
            entry => entry.Key,
            entry => RestoreTourniquet(entry.Value));
        treatment.ProcessedActions.Clear();
        treatment.AuditHistory.Clear();
    }

    private void ApplyEmergencyCareState(EntityUid character, NCEmergencyCareStateSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            RemComp<NCEmergencyCareStateComponent>(character);
            return;
        }

        var emergency = EnsureComp<NCEmergencyCareStateComponent>(character);
        emergency.Profile = snapshot.Profile;
        emergency.CprRemaining = snapshot.CprRemaining;
        emergency.OxygenRemaining = snapshot.OxygenRemaining;
        emergency.VentilationRemaining = snapshot.VentilationRemaining;
        emergency.CrystalloidVolume = snapshot.CrystalloidVolume;
        emergency.ProcessedActions.Clear();
        emergency.AuditHistory.Clear();
    }

    private void ApplySystemicState(EntityUid character, NCSystemicStateSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            RemComp<NCSystemicStateComponent>(character);
            return;
        }

        var systemic = EnsureComp<NCSystemicStateComponent>(character);
        systemic.Profile = snapshot.Profile;
        systemic.StateInitialized = snapshot.Initialized;
        systemic.Burdens = new Dictionary<NCSystemicChannel, float>(snapshot.Burdens);
        systemic.ShockableArrestLatched = snapshot.ShockableArrestLatched;
        systemic.ProcessedPayloads.Clear();
        systemic.ProcessedPayloadOrder.Clear();
    }

    private static NCWoundTreatmentRecord RestoreWoundTreatment(NCWoundTreatmentStateSnapshot record)
    {
        return new NCWoundTreatmentRecord
        {
            WoundId = record.WoundId,
            AppliedFlags = record.AppliedFlags,
            PressureAppliedAt = record.PressureAppliedAt,
            BandagedAt = record.BandagedAt,
            HemostaticAppliedAt = record.HemostaticAppliedAt,
            BandageAbsorptionRemaining = record.BandageAbsorptionRemaining,
            BandageSterility = record.BandageSterility,
            ModifiedAt = record.ModifiedAt,
        };
    }

    private static NCTourniquetRecord RestoreTourniquet(NCTourniquetStateSnapshot record)
    {
        return new NCTourniquetRecord
        {
            RootZone = record.RootZone,
            CoveredWoundIds = [.. record.CoveredWoundIds],
            Active = record.Active,
            AppliedAt = record.AppliedAt,
            IschemiaRiskAt = record.IschemiaRiskAt,
            CriticalIschemiaAt = record.CriticalIschemiaAt,
            RemovedAt = record.RemovedAt,
            ReboundEndsAt = record.ReboundEndsAt,
            ControlledRelease = record.ControlledRelease,
        };
    }

    private void ApplyCirculationState(EntityUid character, NCCirculationStateSnapshot state)
    {
        var circulation = EnsureComp<NCCirculationComponent>(character);
        circulation.Profile = state.Profile;
        circulation.StateInitialized = state.Initialized;
        circulation.BloodVolume = state.BloodVolume;
        circulation.EffectiveCirculatingVolume = state.EffectiveCirculatingVolume;
        circulation.CardiacOutput = state.CardiacOutput;
        circulation.Perfusion = state.Perfusion;
        circulation.BloodPressureState = state.BloodPressureState;
        circulation.HeartRhythm = state.HeartRhythm;
        circulation.TransfusionCompatibility = state.TransfusionCompatibility;
        // Offline time is frozen. The next scheduler interval starts at this runtime's restore boundary.
        circulation.LastEvaluation = _timing.CurTime;
    }

    private void ApplyPhysiologyState(EntityUid character, NCPhysiologyStateSnapshot state)
    {
        var physiology = EnsureComp<NCPhysiologyComponent>(character);
        physiology.Oxygenation = state.Oxygenation;
        physiology.TotalPain = state.TotalPain;
        physiology.PainLevel = state.PainLevel;
        physiology.Shock = state.Shock;
        physiology.ShockStage = state.ShockStage;
        physiology.Consciousness = state.Consciousness;
        physiology.Condition = state.Condition;
        physiology.BrainFunction = state.BrainFunction;
        physiology.TimeBelowCriticalPerfusion = state.TimeBelowCriticalPerfusion;
        physiology.TimeInClinicalDeath = state.TimeInClinicalDeath;
        physiology.Irreversible = state.Irreversible;
    }
}
