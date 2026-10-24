// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Cyberware.Visuals;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    /// <summary>
    /// Returns only slot-installed modules owned by one body. This is a bounded local body-graph query used by the
    /// Living City inventory adapter; it never performs a global entity scan.
    /// </summary>
    public IReadOnlyList<EntityUid> GetInstalledModuleEntities(EntityUid body, int maximum)
    {
        if (maximum <= 0 || !HasComp<NCMedicalBodyComponent>(body))
            return [];
        return CollectCyberware(body)
            .Where(uid => TryComp<NCCyberwareComponent>(uid, out var module) && module.InstalledSlot != null)
            .OrderBy(uid => Comp<NCCyberwareComponent>(uid).StableInstanceId)
            .Take(maximum)
            .ToArray();
    }

    public bool TryGetInstalledModuleEntity(EntityUid body, Guid stableInstanceId, out EntityUid module)
    {
        module = default;
        if (stableInstanceId == Guid.Empty || !HasComp<NCMedicalBodyComponent>(body))
            return false;
        foreach (var candidate in CollectCyberware(body))
        {
            if (TryComp<NCCyberwareComponent>(candidate, out var state) && state.InstalledSlot != null &&
                state.StableInstanceId == stableInstanceId)
            {
                module = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Captures installed slot modules in a stable semantic order. Anatomical replacements are captured by
    /// their category-keyed organ record instead and therefore cannot be duplicated here.
    /// </summary>
    public List<NCCyberwareStateSnapshot> CaptureInstalledModules(EntityUid body)
    {
        var result = new List<NCCyberwareStateSnapshot>();
        foreach (var moduleUid in CollectCyberware(body))
        {
            if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) || module.InstalledSlot == null ||
                !TryResolveSemanticPlacement(body, moduleUid, out var hostCategory, out var slot) ||
                hostCategory == null || slot == null ||
                !TryCaptureState(moduleUid, hostCategory, slot, out var snapshot))
            {
                continue;
            }
            result.Add(snapshot);
        }

        result.Sort((left, right) =>
        {
            var host = StringComparer.Ordinal.Compare(left.HostCategory?.Id, right.HostCategory?.Id);
            if (host != 0)
                return host;
            var slot = StringComparer.Ordinal.Compare(left.Slot?.Id, right.Slot?.Id);
            return slot != 0 ? slot : left.StableInstanceId.CompareTo(right.StableInstanceId);
        });
        return result;
    }

    public bool TryCaptureOrganState(EntityUid organ, out NCCyberwareStateSnapshot snapshot)
    {
        return TryCaptureState(organ, null, null, out snapshot);
    }

    public bool ValidatePersistentState(NCCyberwareStateSnapshot state, bool installedModule)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.EntityPrototype) ||
            state.EntityPrototype.Length > 128 || state.StableInstanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(state.Profile.Id) ||
            !_prototypes.TryIndex(state.Profile, out NCCyberwarePrototype? profile) ||
            !_prototypes.TryIndex<EntityPrototype>(state.EntityPrototype, out var entityPrototype) ||
            !entityPrototype.TryComp<NCCyberwareComponent>(out var definition, _componentFactory) ||
            definition.Profile != state.Profile ||
            !entityPrototype.TryComp<NCOrganIntegrityComponent>(out _, _componentFactory) ||
            !Finite(state.MaximumIntegrity, float.Epsilon, 1_000_000f) ||
            MathF.Abs(state.MaximumIntegrity - profile.MaximumIntegrity) > 0.001f ||
            !Unit(state.PermanentDamage) ||
            !Finite(state.CurrentIntegrity, 0f,
                NCCyberwareCondition.ServiceCeiling(state.MaximumIntegrity, state.PermanentDamage)) ||
            !ValidateAssembly(profile, state.Assembly, state.StableInstanceId) ||
            !ValidatePowerBus(entityPrototype, state) ||
            state.Assembly?.ValidatedRevision != null && state.CurrentIntegrity <= 0f ||
            state.CurrentPower != 0f ||
            !Finite(state.Heat, 0f, profile.HeatCapacity) ||
            !Finite(state.Load, 0f, 1f) ||
            state.EmpDisabledUntil < TimeSpan.FromDays(-3_650_000) ||
            state.EmpDisabledUntil > TimeSpan.FromDays(3_650_000) ||
            !Finite(state.NeuralIntegrity, 0f, 1f) ||
            !Enum.IsDefined(state.NeuralState))
        {
            return false;
        }

        if (!NCCyberwareServiceHistory.Valid(state.Service) || !_prototypes.TryIndex(state.Service!.Firmware, out var firmware) ||
            firmware.CompatibleProfiles.Count > 0 && !firmware.CompatibleProfiles.Contains(state.Profile))
            return false;
        var hasAbility = entityPrototype.TryComp<NCCyberwareAbilityComponent>(out _, _componentFactory);
        var hasExterior = entityPrototype.TryComp<NCCyberwareExteriorComponent>(out _, _componentFactory);
        if (hasExterior != (state.Exterior != null) ||
            state.Exterior is { } exterior && (!_prototypes.HasIndex(exterior.Finish) ||
                !Unit(exterior.SkinColor.R) || !Unit(exterior.SkinColor.G) || !Unit(exterior.SkinColor.B) || exterior.SkinColor.A != 1f))
            return false;
        if (hasAbility != state.AbilityCooldownSeconds.HasValue ||
            state.AbilityCooldownSeconds is { } cooldown && !NCCyberwareAbilityMath.ValidCooldown(cooldown))
            return false;
        ValidateProfile(profile);
        if (installedModule)
        {
            if (profile.Form != NCCyberwareForm.Implant || state.Slot is not { } slotId ||
                state.HostCategory is not { } hostCategory ||
                !profile.CompatibleSlots.Contains(slotId) ||
                !_prototypes.TryIndex(slotId, out NCCyberwareSlotPrototype? slot) ||
                !slot.HostCategories.Contains(hostCategory) || profile.Size > slot.Capacity)
            {
                return false;
            }
        }
        else if (profile.Form == NCCyberwareForm.Implant || state.Slot != null || state.HostCategory != null)
        {
            return false;
        }

        return NeuralStateShapeMatches(state, profile);
    }

    public bool TryApplyPersistentState(EntityUid moduleUid, NCCyberwareStateSnapshot state, TimeSpan runtimeAnchor)
    {
        var installed = state.Slot != null || state.HostCategory != null;
        if (!ValidatePersistentState(state, installed) ||
            !TryComp<NCCyberwareComponent>(moduleUid, out var module) || module.Profile != state.Profile ||
            !TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity))
        {
            return false;
        }

        var previousIdentity = module.StableInstanceId;
        module.StableInstanceId = state.StableInstanceId;
        if (!TryRestoreAssembly(moduleUid, _prototypes.Index(state.Profile), state.Assembly) ||
            !TryRestorePowerBus(moduleUid, state.PowerBus))
        {
            module.StableInstanceId = previousIdentity;
            return false;
        }
        module.StateInitialized = true;
        module.Service = NCCyberwareServiceHistory.Clone(state.Service!);
        module.PersistenceCapturedAt = null;
        if (TryComp<NCCyberwareExteriorComponent>(moduleUid, out var exterior) && state.Exterior is { } finish)
        {
            exterior.Finish = finish.Finish;
            exterior.Ruptured = finish.Ruptured;
            exterior.SkinColor = finish.SkinColor;
            exterior.ObservedIntegrity = state.CurrentIntegrity;
            exterior.ProjectionInitialized = false;
        }
        module.PermanentDamage = state.PermanentDamage;
        module.ThermalShutdown = state.ThermalShutdown;
        module.InstalledSlot = state.Slot;
        module.CurrentPower = state.CurrentPower;
        module.Heat = state.Heat;
        module.Load = state.Load;
        module.ActiveLoadUntil = null;
        if (TryComp<NCCyberwareAbilityComponent>(moduleUid, out var ability))
        {
            ability.CooldownRemainingSeconds = state.AbilityCooldownSeconds!.Value;
            ability.CooldownObservedAt = runtimeAnchor;
            ability.Active = false;
            ability.ActiveUntil = null;
            module.Load = 0f;
        }
        module.EmpDisabledUntil = state.EmpDisabledUntil;
        module.NeuralIntegrity = state.NeuralIntegrity;
        module.NeuralState = state.NeuralState;
        module.Calibrated = state.Calibrated;
        module.LastAdvancedAt = runtimeAnchor;
        integrity.MaxIntegrity = state.MaximumIntegrity;
        integrity.CurrentIntegrity = state.CurrentIntegrity;
        NormalizeMechanicalIntegrity(integrity);

        var profile = _prototypes.Index(state.Profile);
        UpdateNeuralState(module, profile, runtimeAnchor);
        UpdateOperational(moduleUid, module, profile, runtimeAnchor);
        if (TryResolveOwningBody(moduleUid, out _))
            Schedule(moduleUid, profile, true);
        return true;
    }

    /// <summary>
    /// Replaces only slot-mounted modules during medical restore. Existing anatomical replacements are managed
    /// by the category graph restore. Old modules are retained until every desired module is installed so a
    /// failed materialization can restore the previous live graph.
    /// </summary>
    public bool TryRestoreInstalledModules(
        EntityUid body,
        IReadOnlyList<NCCyberwareStateSnapshot> desired,
        TimeSpan runtimeAnchor)
    {
        if (!HasComp<NCMedicalBodyComponent>(body) || desired.Count > MaximumCyberwarePerBody ||
            desired.Any(state => !ValidatePersistentState(state, true)) ||
            desired.Select(state => (state.HostCategory, state.Slot)).Distinct().Count() != desired.Count)
        {
            return false;
        }

        var old = CaptureInstalledRuntime(body);
        var detachedOld = new List<InstalledRuntime>(old.Count);
        foreach (var entry in old)
        {
            if (!TryRemoveForRestore(body, entry.Module))
            {
                RollbackOldModules(body, detachedOld);
                return false;
            }
            detachedOld.Add(entry);
        }

        var spawned = new List<EntityUid>(desired.Count);
        foreach (var state in desired)
        {
            if (state.HostCategory is not { } hostCategory || state.Slot is not { } slot ||
                !TryFindOrgan(body, hostCategory, out var host))
            {
                RollbackRestoredModules(body, spawned, detachedOld);
                return false;
            }

            var module = Spawn(state.EntityPrototype, Transform(body).Coordinates);
            spawned.Add(module);
            var install = TryInstall(new NCCyberwareInstallRequest(
                NCActionResolutionId.New(),
                body,
                host,
                module,
                slot,
                state.NeuralState is NCCyberwareNeuralState.Linked or NCCyberwareNeuralState.Degraded,
                state.Calibrated));
            if (!install.Accepted || !TryApplyPersistentState(module, state, runtimeAnchor))
            {
                RollbackRestoredModules(body, spawned, detachedOld);
                return false;
            }
        }

        foreach (var entry in detachedOld)
        {
            if (Exists(entry.Module))
                QueueDel(entry.Module);
        }
        RemComp<NCCyberwareBodyStateComponent>(body);
        return true;
    }

    private bool TryCaptureState(
        EntityUid moduleUid,
        ProtoId<OrganCategoryPrototype>? hostCategory,
        ProtoId<NCCyberwareSlotPrototype>? slot,
        out NCCyberwareStateSnapshot snapshot)
    {
        snapshot = default!;
        if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryComp<NCOrganIntegrityComponent>(moduleUid, out var integrity) ||
            !TryGetProfile(module.Profile, out _) ||
            MetaData(moduleUid).EntityPrototype?.ID is not { } entityPrototype)
        {
            return false;
        }

        snapshot = new NCCyberwareStateSnapshot
        {
            EntityPrototype = entityPrototype,
            Service = NCCyberwareServiceHistory.Clone(module.Service),
            Profile = module.Profile,
            StableInstanceId = module.StableInstanceId,
            Slot = slot,
            HostCategory = hostCategory,
            CurrentIntegrity = integrity.CurrentIntegrity,
            MaximumIntegrity = integrity.MaxIntegrity,
            CurrentPower = module.CurrentPower,
            Heat = module.Heat,
            Load = module.Load,
            EmpDisabledUntil = module.EmpDisabledUntil,
            NeuralIntegrity = module.NeuralIntegrity,
            NeuralState = module.NeuralState,
            Calibrated = module.Calibrated,
            PermanentDamage = module.PermanentDamage,
            Assembly = CaptureAssembly(moduleUid),
            PowerBus = CapturePowerBus(moduleUid),
            ThermalShutdown = module.ThermalShutdown,
            Exterior = TryComp<NCCyberwareExteriorComponent>(moduleUid, out var exterior)
                ? new NCCyberwareExteriorSnapshot
                {
                    Finish = exterior.Finish,
                    Ruptured = exterior.Ruptured,
                    SkinColor = exterior.SkinColor ?? (TryComp<VisualOrganComponent>(moduleUid, out var visual) ? visual.Profile.SkinColor : Color.White),
                } : null,
            AbilityCooldownSeconds = TryComp<NCCyberwareAbilityComponent>(moduleUid, out var ability)
                ? NCCyberwareAbilityMath.Remaining(ability.CooldownRemainingSeconds, ability.CooldownObservedAt, _timing.CurTime)
                : null,
        };
        return true;
    }

    private List<InstalledRuntime> CaptureInstalledRuntime(EntityUid body)
    {
        var result = new List<InstalledRuntime>();
        foreach (var module in CollectCyberware(body))
        {
            if (TryResolveSemanticPlacement(body, module, out var host, out var slot) &&
                host is { } hostCategory && slot is { } slotId)
            {
                if (TryCaptureState(module, hostCategory, slotId, out var snapshot))
                    result.Add(new InstalledRuntime(module, hostCategory, slotId, snapshot));
            }
        }
        return result;
    }

    private bool TryRemoveForRestore(EntityUid body, EntityUid module)
    {
        return TryRemove(new NCCyberwareRemoveRequest(NCActionResolutionId.New(), body, module)).Accepted;
    }

    private void RollbackRestoredModules(
        EntityUid body,
        IReadOnlyList<EntityUid> spawned,
        IReadOnlyList<InstalledRuntime> old)
    {
        foreach (var module in spawned.Reverse())
        {
            if (!Exists(module))
                continue;
            if (TryComp<NCCyberwareComponent>(module, out var state) && state.InstalledSlot != null)
                TryRemoveForRestore(body, module);
            QueueDel(module);
        }
        RollbackOldModules(body, old);
        RemComp<NCCyberwareBodyStateComponent>(body);
    }

    private void RollbackOldModules(EntityUid body, IReadOnlyList<InstalledRuntime> old)
    {
        foreach (var entry in old)
        {
            if (!Exists(entry.Module) || !TryFindOrgan(body, entry.HostCategory, out var host))
                continue;
            TryInstall(new NCCyberwareInstallRequest(
                NCActionResolutionId.New(),
                body,
                host,
                entry.Module,
                entry.Slot));
            TryApplyPersistentState(entry.Module, entry.State, _timing.CurTime);
        }
    }

    private bool TryFindOrgan(
        EntityUid body,
        ProtoId<OrganCategoryPrototype> category,
        out EntityUid organUid)
    {
        organUid = default;
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return false;
        foreach (var candidate in bodyComponent.Organs.ContainedEntities)
        {
            if (TryComp<OrganComponent>(candidate, out var organ) && organ.Category == category)
            {
                organUid = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool NeuralStateShapeMatches(
        NCCyberwareStateSnapshot state,
        NCCyberwarePrototype profile)
    {
        if (profile.NeuralRequirement == NCCyberwareNeuralRequirement.None)
            return state.NeuralState == NCCyberwareNeuralState.NotApplicable && state.Calibrated;
        return state.NeuralState != NCCyberwareNeuralState.NotApplicable &&
               (state.Calibrated || state.NeuralState is NCCyberwareNeuralState.Disconnected or NCCyberwareNeuralState.Disrupted);
    }

    private static bool Finite(float value, float minimum, float maximum)
    {
        return float.IsFinite(value) && value >= minimum && value <= maximum;
    }

    private readonly record struct InstalledRuntime(
        EntityUid Module,
        ProtoId<OrganCategoryPrototype> HostCategory,
        ProtoId<NCCyberwareSlotPrototype> Slot,
        NCCyberwareStateSnapshot State);
}
