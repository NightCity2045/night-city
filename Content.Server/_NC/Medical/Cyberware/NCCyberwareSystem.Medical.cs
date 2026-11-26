// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Internal;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
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
}
