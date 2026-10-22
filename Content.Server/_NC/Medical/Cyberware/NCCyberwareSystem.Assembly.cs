// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    private const int MaximumAssemblyParts = 8;

    private void InitializeAssemblyEvents()
    {
        SubscribeLocalEvent<NCCyberwarePartComponent, MapInitEvent>(OnPartMapInit);
        SubscribeLocalEvent<NCCyberwareAssemblyComponent, EntInsertedIntoContainerMessage>(OnAssemblyInserted);
        SubscribeLocalEvent<NCCyberwareAssemblyComponent, EntRemovedFromContainerMessage>(OnAssemblyRemoved);
    }

    private void OnPartMapInit(Entity<NCCyberwarePartComponent> part, ref MapInitEvent args)
    {
        var profile = _prototypes.Index(part.Comp.Profile);
        if (!Positive(profile.MaximumIntegrity) || !Unit(profile.PermanentDamageThresholdFraction) ||
            !NonNegative(profile.PermanentDamageScale))
            throw new InvalidOperationException($"Invalid cyberware part profile {profile.ID}.");
        var integrity = EnsureComp<NCOrganIntegrityComponent>(part);
        if (part.Comp.StableInstanceId == Guid.Empty)
            part.Comp.StableInstanceId = Guid.NewGuid();
        if (!part.Comp.Initialized)
        {
            part.Comp.Initialized = true;
            integrity.MaxIntegrity = profile.MaximumIntegrity;
            integrity.CurrentIntegrity = profile.MaximumIntegrity;
        }
        NormalizePart(part.Comp, integrity, profile);
    }

    private void OnAssemblyInserted(Entity<NCCyberwareAssemblyComponent> assembly,
        ref EntInsertedIntoContainerMessage args)
    {
        if (assembly.Comp.Parts.Values.Contains(args.Container))
            AssemblyChanged(assembly);
    }

    private void OnAssemblyRemoved(Entity<NCCyberwareAssemblyComponent> assembly,
        ref EntRemovedFromContainerMessage args)
    {
        if (assembly.Comp.Parts.Values.Contains(args.Container))
            AssemblyChanged(assembly);
    }

    private void InitializeAssembly(EntityUid uid, NCCyberwareComponent module, NCCyberwarePrototype profile)
    {
        if (profile.Assembly == null)
            return;
        var assembly = EnsureComp<NCCyberwareAssemblyComponent>(uid);
        EnsureAssemblyContainers(uid, assembly, _prototypes.Index(profile.Assembly.Value));
        if (!assembly.Initialized)
        {
            var initial = CreateDefaultAssemblySnapshot(module.Profile, module.StableInstanceId);
            if (initial == null || !TryRestoreAssembly(uid, profile, initial))
                throw new InvalidOperationException($"Cannot initialize cyberware assembly {uid}.");
        }
    }

    private void EnsureAssemblyContainers(EntityUid uid, NCCyberwareAssemblyComponent assembly,
        NCCyberwareAssemblyPrototype profile)
    {
        if (profile.DefaultParts.Count is < 1 or > MaximumAssemblyParts || !Positive(profile.RepairPerUnit) ||
            !Unit(profile.FrameDamageThresholdFraction) || !Unit(profile.InternalDamageThresholdFraction) ||
            !NonNegative(profile.FramePermanentDamageScale) || !NonNegative(profile.HeatPermanentDamageScale))
            throw new InvalidOperationException($"Invalid cyberware assembly {profile.ID}.");
        foreach (var slot in profile.DefaultParts.Keys)
        {
            if (!assembly.Parts.ContainsKey(slot))
                assembly.Parts.Add(slot, _containers.EnsureContainer<ContainerSlot>(uid, $"nc_part_{slot.Id}"));
        }
    }

    private void AssemblyChanged(Entity<NCCyberwareAssemblyComponent> assembly)
    {
        if (assembly.Comp.Restoring || TerminatingOrDeleted(assembly) ||
            !TryComp<NCCyberwareComponent>(assembly, out var module) ||
            !TryGetProfile(module.Profile, out var profile))
            return;
        // Saturate instead of wrapping into a revision that an old service action could reuse.
        assembly.Comp.ConfigurationRevision = assembly.Comp.ConfigurationRevision == uint.MaxValue
            ? uint.MaxValue
            : assembly.Comp.ConfigurationRevision + 1;
        assembly.Comp.ValidatedRevision = null;
        RefreshAssembly(assembly.Comp);
        UpdateOperational(assembly, module, profile, _timing.CurTime);
        if (TryResolveOwningBody(assembly, out var body))
            MarkBodyChanged(body);
    }

    private void RefreshAssembly(NCCyberwareAssemblyComponent assembly)
    {
        var efficiency = 1f;
        foreach (var container in assembly.Parts.Values)
        {
            if (container.ContainedEntity is not { } uid ||
                !TryComp<NCCyberwarePartComponent>(uid, out var part) ||
                !TryComp<NCOrganIntegrityComponent>(uid, out var integrity))
            {
                efficiency = 0f;
                break;
            }
            var profile = _prototypes.Index(part.Profile);
            efficiency = MathF.Min(efficiency,
                NCCyberwareCondition.Efficiency(integrity.CurrentIntegrity, profile.MaximumIntegrity));
        }
        assembly.FunctionalEfficiency = efficiency;
    }

    private static void NormalizePart(NCCyberwarePartComponent part, NCOrganIntegrityComponent integrity,
        NCCyberwarePartPrototype profile)
    {
        integrity.MaxIntegrity = profile.MaximumIntegrity;
        integrity.CurrentIntegrity = Math.Clamp(integrity.CurrentIntegrity, 0f,
            NCCyberwareCondition.ServiceCeiling(profile.MaximumIntegrity, part.PermanentDamage));
        NormalizeMechanicalIntegrity(integrity);
    }

    /// <summary>Distributes only damage transmitted beyond the frame's safe impact threshold to internal nodes.</summary>
    private void DamageAssembly(EntityUid uid, NCCyberwareComponent module, NCCyberwarePrototype profile,
        float damage, bool continuousHeat = false)
    {
        if (profile.Assembly == null || !TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly))
            return;
        var definition = _prototypes.Index(profile.Assembly.Value);
        module.PermanentDamage = NCCyberwareCondition.PermanentDamageAfterImpact(
            module.PermanentDamage, damage, profile.MaximumIntegrity,
            continuousHeat ? 0f : definition.FrameDamageThresholdFraction,
            continuousHeat ? definition.HeatPermanentDamageScale : definition.FramePermanentDamageScale);
        var frame = Comp<NCOrganIntegrityComponent>(uid);
        frame.CurrentIntegrity = MathF.Min(frame.CurrentIntegrity,
            NCCyberwareCondition.ServiceCeiling(profile.MaximumIntegrity, module.PermanentDamage));
        NormalizeMechanicalIntegrity(frame);
        var internalDamage = MathF.Max(0f,
            damage - (continuousHeat ? 0f : profile.MaximumIntegrity * definition.InternalDamageThresholdFraction)) /
            assembly.Parts.Count;
        foreach (var container in assembly.Parts.Values)
        {
            if (container.ContainedEntity is not { } partUid ||
                !TryComp<NCCyberwarePartComponent>(partUid, out var part) ||
                !TryComp<NCOrganIntegrityComponent>(partUid, out var integrity))
                continue;
            var partProfile = _prototypes.Index(part.Profile);
            part.PermanentDamage = NCCyberwareCondition.PermanentDamageAfterImpact(part.PermanentDamage,
                internalDamage, partProfile.MaximumIntegrity,
                continuousHeat ? 0f : partProfile.PermanentDamageThresholdFraction,
                continuousHeat ? definition.HeatPermanentDamageScale : partProfile.PermanentDamageScale);
            integrity.CurrentIntegrity = MathF.Max(0f, integrity.CurrentIntegrity - internalDamage);
            NormalizePart(part, integrity, partProfile);
        }
        assembly.ValidatedRevision = null;
        RefreshAssembly(assembly);
    }

    /// <summary>Deterministic one-time migration of a previously monolithic factory item, not a gameplay refill.</summary>
    public NCCyberwareAssemblySnapshot? CreateDefaultAssemblySnapshot(
        ProtoId<NCCyberwarePrototype> profileId, Guid instanceId)
    {
        if (!_prototypes.TryIndex(profileId, out var profile) || profile.Assembly is not { } assemblyId)
            return null;
        var definition = _prototypes.Index(assemblyId);
        var snapshot = new NCCyberwareAssemblySnapshot();
        foreach (var (slot, prototype) in definition.DefaultParts.OrderBy(pair => pair.Key.Id, StringComparer.Ordinal))
        {
            var entity = _prototypes.Index(prototype);
            if (!entity.TryComp<NCCyberwarePartComponent>(out var part, _componentFactory))
                throw new InvalidOperationException($"Missing physical part on {prototype}.");
            var partProfile = _prototypes.Index(part.Profile);
            uint stream = 2166136261;
            foreach (var character in slot.Id)
                stream = unchecked((stream ^ character) * 16777619);
            snapshot.Parts.Add(new NCCyberwarePartSnapshot
            {
                Slot = slot,
                EntityPrototype = prototype.Id,
                StableInstanceId = new NCActionResolutionId(instanceId).DeriveGuid(0x43595054, stream),
                CurrentIntegrity = partProfile.MaximumIntegrity,
            });
        }
        return snapshot;
    }

    private bool ValidateAssembly(NCCyberwarePrototype profile, NCCyberwareAssemblySnapshot? snapshot, Guid rootId)
    {
        if (profile.Assembly == null)
            return snapshot == null;
        if (snapshot?.Parts == null || snapshot.ConfigurationRevision == 0 ||
            snapshot.ValidatedRevision is { } revision && revision != snapshot.ConfigurationRevision ||
            snapshot.Parts.Count > MaximumAssemblyParts)
            return false;
        var definition = _prototypes.Index(profile.Assembly.Value);
        var slots = new HashSet<ProtoId<NCCyberwarePartSlotPrototype>>();
        var ids = new HashSet<Guid> { rootId };
        foreach (var part in snapshot.Parts)
        {
            if (part == null || !definition.DefaultParts.ContainsKey(part.Slot) || !slots.Add(part.Slot) ||
                part.StableInstanceId == Guid.Empty || !ids.Add(part.StableInstanceId) ||
                string.IsNullOrWhiteSpace(part.EntityPrototype) || part.EntityPrototype.Length > 128 ||
                !_prototypes.TryIndex<EntityPrototype>(part.EntityPrototype, out var entity) ||
                !entity.TryComp<NCCyberwarePartComponent>(out var component, _componentFactory) ||
                !_prototypes.TryIndex(component.Profile, out var partProfile) || partProfile.Slot != part.Slot ||
                !Unit(part.PermanentDamage) ||
                !Finite(part.CurrentIntegrity, 0f,
                    NCCyberwareCondition.ServiceCeiling(partProfile.MaximumIntegrity, part.PermanentDamage)))
                return false;
        }
        return snapshot.ValidatedRevision == null || slots.Count == definition.DefaultParts.Count &&
            snapshot.Parts.All(part => part.CurrentIntegrity > 0f);
    }

    private NCCyberwareAssemblySnapshot? CaptureAssembly(EntityUid uid)
    {
        if (!TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly))
            return null;
        var snapshot = new NCCyberwareAssemblySnapshot
        {
            ConfigurationRevision = assembly.ConfigurationRevision,
            ValidatedRevision = assembly.ValidatedRevision,
        };
        foreach (var (slot, container) in assembly.Parts.OrderBy(pair => pair.Key.Id, StringComparer.Ordinal))
        {
            if (container.ContainedEntity is not { } partUid)
                continue;
            var part = Comp<NCCyberwarePartComponent>(partUid);
            snapshot.Parts.Add(new NCCyberwarePartSnapshot
            {
                Slot = slot,
                EntityPrototype = MetaData(partUid).EntityPrototype!.ID,
                StableInstanceId = part.StableInstanceId,
                CurrentIntegrity = Comp<NCOrganIntegrityComponent>(partUid).CurrentIntegrity,
                PermanentDamage = part.PermanentDamage,
            });
        }
        return snapshot;
    }

    private bool TryRestoreAssembly(EntityUid uid, NCCyberwarePrototype profile, NCCyberwareAssemblySnapshot? snapshot)
    {
        if (profile.Assembly == null)
            return snapshot == null;
        if (!ValidateAssembly(profile, snapshot, Comp<NCCyberwareComponent>(uid).StableInstanceId))
            return false;
        var assembly = EnsureComp<NCCyberwareAssemblyComponent>(uid);
        EnsureAssemblyContainers(uid, assembly, _prototypes.Index(profile.Assembly.Value));
        var old = assembly.Parts.Where(pair => pair.Value.ContainedEntity != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ContainedEntity!.Value);
        var spawned = new List<EntityUid>();
        var succeeded = false;
        assembly.Restoring = true;
        try
        {
            foreach (var (slot, partUid) in old)
            {
                if (!_containers.Remove(partUid, assembly.Parts[slot], destination: Transform(uid).Coordinates))
                    return false;
            }
            foreach (var state in snapshot!.Parts)
            {
                var partUid = Spawn(state.EntityPrototype, Transform(uid).Coordinates);
                spawned.Add(partUid);
                var part = Comp<NCCyberwarePartComponent>(partUid);
                part.StableInstanceId = state.StableInstanceId;
                part.PermanentDamage = state.PermanentDamage;
                var integrity = Comp<NCOrganIntegrityComponent>(partUid);
                integrity.CurrentIntegrity = state.CurrentIntegrity;
                NormalizePart(part, integrity, _prototypes.Index(part.Profile));
                if (!_containers.Insert(partUid, assembly.Parts[state.Slot]))
                    return false;
            }
            assembly.ConfigurationRevision = snapshot.ConfigurationRevision;
            assembly.ValidatedRevision = snapshot.ValidatedRevision;
            assembly.Initialized = true;
            foreach (var partUid in old.Values)
                QueueDel(partUid);
            old.Clear();
            succeeded = true;
            return true;
        }
        finally
        {
            if (!succeeded)
            {
                foreach (var partUid in spawned)
                {
                    if (_containers.TryGetContainingContainer((partUid, null, null), out var container))
                        _containers.Remove(partUid, container);
                    QueueDel(partUid);
                }
                foreach (var (slot, partUid) in old)
                {
                    if (assembly.Parts[slot].ContainedEntity != partUid)
                        _containers.Insert(partUid, assembly.Parts[slot]);
                }
            }
            assembly.Restoring = false;
            RefreshAssembly(assembly);
        }
    }
}
