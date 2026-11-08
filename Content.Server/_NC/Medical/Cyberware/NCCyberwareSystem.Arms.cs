// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Arms;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    public bool CanFitArmModule(EntityUid arm, EntityUid item, bool restoring = false)
    {
        if (!TryComp<NCArmModuleComponent>(item, out var module)) return true;
        if (!TryComp<NCArmFrameComponent>(arm, out var frame))
            return restoring && module.Kind is NCArmModuleKind.Tool or NCArmModuleKind.Mantis;
        if (!TryComp<NCCyberwareSlotHostComponent>(arm, out var host) || host.Modules == null ||
            !restoring && module.RequiresHeavyFrame && (!frame.Heavy || ArmStrength(arm) <= 1f)) return false;
        var used = 0f;
        var socket = false;
        foreach (var uid in host.Modules.ContainedEntities)
        {
            if (uid == item || !TryComp<NCArmModuleComponent>(uid, out var other)) continue;
            if (other.Kind == module.Kind) return false;
            used += other.Volume;
            socket |= other.Kind == NCArmModuleKind.QuickSocket;
        }
        return (restoring || !module.RequiresQuickSocket || socket) && NCArmHardwareMath.Fits(frame.Volume, used, module.Volume);
    }

    public bool ArmHardwareReady(EntityUid uid)
    {
        if (!TryComp<NCArmModuleComponent>(uid, out _)) return true;
        return TryComp<ChildOrganComponent>(uid, out var child) && child.Parent is { } arm &&
               TryComp<NCArmFrameComponent>(arm, out var frame) && !frame.PanelOpen &&
               TryComp<NCCyberwareComponent>(arm, out var hardware) && hardware.Operational && CanFitArmModule(arm, uid);
    }

    public float ArmStrength(EntityUid arm)
    {
        if (!TryComp<NCCyberwareAssemblyComponent>(arm, out var assembly)) return 1f;
        foreach (var socket in assembly.Parts.Values)
            if (socket.ContainedEntity is { } uid && TryComp<NCArmDriveComponent>(uid, out var drive) &&
                TryComp<Content.Shared._NC.Medical.Anatomy.NCOrganIntegrityComponent>(uid, out var integrity) && integrity.CurrentIntegrity > 0)
                return 1 + (drive.Strength - 1) * Math.Clamp(assembly.FunctionalEfficiency, 0, 1) * Math.Min(QualityPerformance(arm), 1.5f);
        return 1f;
    }

    public bool TryServiceArmPart(EntityUid arm, EntityUid user, EntityUid? inserted, string slotId)
    {
        if (!TryComp<NCArmFrameComponent>(arm, out var frame) || !frame.PanelOpen ||
            !TryComp<NCCyberwareAssemblyComponent>(arm, out var assembly) || assembly.ConfigurationRevision == uint.MaxValue ||
            !_assemblyBlocker.CanInteract(user, arm) || !_assemblyInteraction.InRangeUnobstructed(user, arm, 2f)) return false;
        if (inserted is { } uid)
        {
            if (!_assemblyHands.IsHolding(user, uid) || !TryComp<NCCyberwarePartComponent>(uid, out var part)) return false;
            var profile = _prototypes.Index(part.Profile);
            var socket = assembly.Parts.FirstOrDefault(pair => (pair.Key == profile.Slot || profile.CompatibleSlots.Contains(pair.Key)) && pair.Value.ContainedEntity == null).Value;
            return socket != null && _assemblyHands.TryDropIntoContainer(user, uid, socket);
        }
        if (!assembly.Parts.TryGetValue(new ProtoId<NCCyberwarePartSlotPrototype>(slotId), out var container) ||
            container.ContainedEntity is not { } removed || !_containers.Remove(removed, container, destination: Transform(user).Coordinates)) return false;
        _assemblyHands.TryPickupAnyHand(user, removed);
        return true;
    }
}
