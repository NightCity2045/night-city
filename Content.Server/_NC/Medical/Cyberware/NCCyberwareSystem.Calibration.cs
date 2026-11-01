// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Power.Components;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    /// <summary>Mounting is already committed. A failed final test never undoes the physical installation.</summary>
    public NCFreeformSurgeryFailure TryFinishMount(EntityUid uid, NCActionResolutionId resolution)
    {
        if (!TryComp<NCCyberwareComponent>(uid, out var module) || !TryGetProfile(module.Profile, out var profile))
            return NCFreeformSurgeryFailure.NoApplicableTarget;
        if (profile.RequiresSeparateCalibration)
            return NCFreeformSurgeryFailure.CyberwareCalibrationRequired;
        TryCalibrateCyberware((uid, module), resolution, out var failure);
        return failure;
    }

    /// <summary>Read-only signal, force and full-load supply check against the actual attached hardware.</summary>
    public bool CanCalibrateCyberware(Entity<NCCyberwareComponent?> entity, out NCFreeformSurgeryFailure failure)
    {
        return CanCalibrateCyberware(entity, out failure, out _);
    }

    private bool CanCalibrateCyberware(Entity<NCCyberwareComponent?> entity,
        out NCFreeformSurgeryFailure failure, out Entity<BatteryComponent>? source)
    {
        source = null;
        failure = NCFreeformSurgeryFailure.NoApplicableTarget;
        if (!Resolve(entity, ref entity.Comp, false) || !TryGetProfile(entity.Comp.Profile, out var profile) ||
            !TryResolveOwningBody(entity, out var body) ||
            !TryComp<NCOrganIntegrityComponent>(entity, out var frame))
            return false;
        failure = NCFreeformSurgeryFailure.CyberwareEmpDisabled;
        if (entity.Comp.EmpDisabledUntil > _timing.CurTime)
            return false;
        failure = NCFreeformSurgeryFailure.CyberwareOverheated;
        if (entity.Comp.ThermalShutdown || entity.Comp.Heat >= profile.HeatDeratingThreshold)
            return false;
        failure = NCFreeformSurgeryFailure.CyberwareSignalWeak;
        if (profile.NeuralRequirement != NCCyberwareNeuralRequirement.None &&
            entity.Comp.NeuralIntegrity < profile.NeuralDegradedThreshold)
            return false;
        var output = NCCyberwareCondition.Efficiency(frame.CurrentIntegrity, profile.MaximumIntegrity);
        if (TryComp<NCCyberwareAssemblyComponent>(entity, out var assembly))
        {
            foreach (var slot in assembly.Parts.Values)
            {
                failure = NCFreeformSurgeryFailure.CyberwareAssemblyIncomplete;
                if (slot.ContainedEntity is not { } partUid ||
                    !TryComp<NCCyberwarePartComponent>(partUid, out var part) ||
                    !TryComp<NCOrganIntegrityComponent>(partUid, out var integrity))
                    return false;
                output = MathF.Min(output, NCCyberwareCondition.Efficiency(integrity.CurrentIntegrity,
                    _prototypes.Index(part.Profile).MaximumIntegrity));
            }
        }
        failure = NCFreeformSurgeryFailure.CyberwareOutputLow;
        if (output < profile.CalibrationMinimumEfficiency)
            return false;
        if (profile.RequiresPower)
        {
            var modules = CollectCyberware(body);
            var bus = FindPowerBus(modules, _timing.CurTime);
            failure = NCFreeformSurgeryFailure.CyberwareNoPower;
            if (bus is not { } holder)
                return false;
            var main = GetBusCell(holder, NCCyberwarePowerBusComponent.MainSlot);
            var reserve = GetBusCell(holder, NCCyberwarePowerBusComponent.ReserveSlot);
            var mainReady = main is { } primary && _battery.GetCharge(primary.AsNullable()) >= profile.CalibrationEnergy;
            var reserveReady = reserve is { } backup && _battery.GetCharge(backup.AsNullable()) >= profile.CalibrationEnergy;
            if (!mainReady && !(reserveReady && profile.PowerPriority == NCCyberwarePowerPriority.LifeSupport))
                return false;
            var demands = new List<NCCyberwarePowerDemand>(modules.Count);
            foreach (var uid in modules)
            {
                var state = Comp<NCCyberwareComponent>(uid);
                var definition = _prototypes.Index(state.Profile);
                if (!definition.RequiresPower || !CanRunElectronics(uid, state, definition, _timing.CurTime))
                    continue;
                demands.Add(new NCCyberwarePowerDemand(state.StableInstanceId, definition.PowerPriority,
                    definition.IdlePowerPerSecond + definition.LoadedPowerPerSecond *
                    (uid == entity.Owner ? 1f : EffectivePowerLoad(state, definition, _timing.CurTime))));
            }
            var width = holder.Comp.MaximumPower * NCCyberwareCondition.Efficiency(
                Comp<NCOrganIntegrityComponent>(holder).CurrentIntegrity,
                _prototypes.Index(Comp<NCCyberwareComponent>(holder).Profile).MaximumIntegrity);
            var grant = NCCyberwarePowerMath.Allocate(demands, width, mainReady, reserveReady)
                .FirstOrDefault(value => value.Id == entity.Comp.StableInstanceId);
            failure = NCFreeformSurgeryFailure.CyberwarePowerOverload;
            if (grant.Source == NCCyberwarePowerSource.None)
                return false;
            source = grant.Source == NCCyberwarePowerSource.Main ? main : reserve;
        }
        failure = NCFreeformSurgeryFailure.None;
        return true;
    }

    /// <summary>Trusted effect API. The surgical field owns physical access and held-tool authorization.</summary>
    public bool TryCalibrateCyberware(Entity<NCCyberwareComponent?> entity, NCActionResolutionId resolution,
        out NCFreeformSurgeryFailure failure)
    {
        failure = NCFreeformSurgeryFailure.NoApplicableTarget;
        if (!resolution.IsValid || !Resolve(entity, ref entity.Comp, false) ||
            !TryResolveOwningBody(entity, out var body))
            return false;
        var key = new NCCyberwareProcessedActionKey(resolution.Value, NCCyberwareActionKind.Calibration, 0, 0);
        if (IsDuplicate(body, key))
        {
            failure = NCFreeformSurgeryFailure.None;
            return true;
        }
        TryAdvanceBodyPower(body, _timing.CurTime);
        if (!CanCalibrateCyberware(entity, out failure, out var source))
            return false;
        var profile = _prototypes.Index(entity.Comp.Profile);
        if (source is { } battery && !_battery.TryUseCharge(battery.AsNullable(), profile.CalibrationEnergy))
        {
            failure = NCFreeformSurgeryFailure.CyberwareNoPower;
            return false;
        }
        ApplyRequestedNeuralConnection(entity.Comp, profile, true, true);
        if (TryComp<NCCyberwareAssemblyComponent>(entity, out var assembly))
            assembly.ValidatedRevision = assembly.ConfigurationRevision;
        Remember(body, key);
        RefreshBodyPower(body, _timing.CurTime);
        MarkBodyChanged(body);
        return true;
    }

    /// <summary>Called only after a committed attachment boundary change; failed transfers retain calibration.</summary>
    public void InvalidateAttachmentCalibration(EntityUid host)
    {
        InvalidateCalibration(host);
        if (!TryComp<NCCyberwareSlotHostComponent>(host, out var slots) || slots.Modules == null)
            return;
        foreach (var uid in slots.Modules.ContainedEntities)
            InvalidateCalibration(uid);
    }

    private void InvalidateCalibration(EntityUid uid)
    {
        if (!TryComp<NCCyberwareComponent>(uid, out var module) || !TryGetProfile(module.Profile, out var profile))
            return;
        module.Calibrated = profile.NeuralRequirement == NCCyberwareNeuralRequirement.None;
        module.NeuralState = module.Calibrated ? NCCyberwareNeuralState.NotApplicable : NCCyberwareNeuralState.Disconnected;
        module.Load = 0f;
        if (!TryResolveOwningBody(uid, out _))
            module.PowerGranted = false;
        UpdateOperational(uid, module, profile, _timing.CurTime);
    }

    /// <summary>One consumable service use repairs one real node. Missing/destroyed parts require the bench.</summary>
    public bool TryApplyLocalService(Entity<NCCyberwareComponent?> entity, NCActionResolutionId resolution)
    {
        if (!resolution.IsValid || !Resolve(entity, ref entity.Comp, false) ||
            !TryResolveOwningBody(entity, out var body) || !TryGetProfile(entity.Comp.Profile, out var profile) ||
            !TryComp<NCOrganIntegrityComponent>(entity, out var frame))
            return false;
        var key = new NCCyberwareProcessedActionKey(resolution.Value, NCCyberwareActionKind.Service, 0, 0);
        if (IsDuplicate(body, key))
            return false;
        TryAdvanceBodyPower(body, _timing.CurTime);
        var repair = _prototypes.Index(profile.RepairProfile);
        if (CalculateServiceNeed(entity.Comp, frame, profile, repair) > 0f)
            return TryApplyService(entity, resolution, profile.RepairProfile, 1f).Status == NCCyberwareMutationStatus.Applied;
        if (!TryComp<NCCyberwareAssemblyComponent>(entity, out var assembly))
            return false;
        foreach (var slot in assembly.Parts.OrderBy(pair => pair.Key.Id, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            if (slot.ContainedEntity is not { } uid || !TryComp<NCCyberwarePartComponent>(uid, out var part) ||
                !TryComp<NCOrganIntegrityComponent>(uid, out var integrity))
                continue;
            var ceiling = NCCyberwareCondition.ServiceCeiling(_prototypes.Index(part.Profile).MaximumIntegrity, part.PermanentDamage);
            if (integrity.CurrentIntegrity >= ceiling)
                continue;
            TryAdvanceBodyPower(body, _timing.CurTime);
            integrity.CurrentIntegrity = MathF.Min(ceiling, integrity.CurrentIntegrity + repair.IntegrityPerUnit);
            NormalizeMechanicalIntegrity(integrity);
            RefreshAssembly(assembly);
            Remember(body, key);
            RefreshBodyPower(body, _timing.CurTime);
            MarkBodyChanged(body);
            return true;
        }
        return false;
    }
}
