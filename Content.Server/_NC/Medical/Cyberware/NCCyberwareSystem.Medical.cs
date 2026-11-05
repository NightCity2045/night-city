// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Internal;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    public ProtoId<NCCyberwareQualityPrototype>? GetAssemblyQuality(EntityUid uid)
    {
        var grade = TryComp<NCCyberwareComponent>(uid, out var module) ? module.Quality : new ProtoId<NCCyberwareQualityPrototype>("NCCyberwareQualityStandard");
        if (!TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly)) return grade;
        NCCyberwareQualityPrototype? result = _prototypes.Index(grade);
        foreach (var (slot, container) in assembly.Parts)
        {
            if (assembly.OptionalSlots.Contains(slot)) continue;
            if (container.ContainedEntity is not { } part || !TryComp<NCCyberwarePartComponent>(part, out var component)) return null;
            var quality = _prototypes.Index(_prototypes.Index(component.Profile).Quality);
            if (result == null || quality.Performance < result.Performance) result = quality;
        }
        if (result == null) return null;
        return new ProtoId<NCCyberwareQualityPrototype>(result.ID);
    }

    public float QualityPerformance(EntityUid uid) =>
        GetAssemblyQuality(uid) is { } grade ? _prototypes.Index(grade).Performance :
        TryComp<NCCyberwareComponent>(uid, out var frame) ? _prototypes.Index(frame.Quality).Performance : 1f;

    public bool MedicalControllerReady(EntityUid uid)
    {
        if (!TryComp<NCMedicalImplantComponent>(uid, out var medical)) return true;
        return TryGetMedicalPart(uid, "NCMedicalControllerSlot", out var controller) &&
               TryComp<NCMedicalControllerComponent>(controller, out var board) && board.Firmware == medical.RequiredFirmware;
    }

    public bool TryGetMedicalPart(EntityUid uid, ProtoId<NCCyberwarePartSlotPrototype> slot, out EntityUid part)
    {
        part = default;
        if (!TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly) ||
            !assembly.Parts.TryGetValue(slot, out var container) || container.ContainedEntity is not { } contained) return false;
        part = contained;
        return true;
    }

    public NCCyberwareQualityPrototype MedicalPartQuality(EntityUid uid, ProtoId<NCCyberwarePartSlotPrototype> slot)
    {
        var quality = TryGetMedicalPart(uid, slot, out var part) && TryComp<NCCyberwarePartComponent>(part, out var component)
            ? _prototypes.Index(component.Profile).Quality : new ProtoId<NCCyberwareQualityPrototype>("NCCyberwareQualityStandard");
        var selected = _prototypes.Index(quality);
        if (TryGetMedicalPart(uid, "NCMedicalControllerSlot", out var controller) && TryComp<NCCyberwarePartComponent>(controller, out var board))
        {
            var controlQuality = _prototypes.Index(_prototypes.Index(board.Profile).Quality);
            if (controlQuality.Performance < selected.Performance) selected = controlQuality;
        }
        if (TryComp<NCCyberwareComponent>(uid, out var frame))
        {
            var frameQuality = _prototypes.Index(frame.Quality);
            if (frameQuality.Performance < selected.Performance) selected = frameQuality;
        }
        return selected;
    }
}
