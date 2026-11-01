// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    /// <summary>Read-only service access to an installed module, using the same real surgical exposure as its power bus.</summary>
    public bool CanServiceFirmware(EntityUid moduleUid, EntityUid user, out Guid identity, out uint configurationRevision)
    {
        identity = default; configurationRevision = 0;
        if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) || module.StableInstanceId == Guid.Empty ||
            !TryResolveOwningBody(moduleUid, out var body) || !_assemblyBlocker.CanInteract(user, body) ||
            !_assemblyInteraction.InRangeUnobstructed(user, body, 2f) || !CanCalibrateCyberware((moduleUid, module), out _)) return false;
        if (module.InstalledSlot == null && TryComp<NCSurgicalSiteComponent>(moduleUid, out var direct))
        {
            if (!direct.BoneCasingOpen || !direct.TissuesRetracted || direct.AccessDepth is not
                (NCSurgicalAccessDepth.OrganExposed or NCSurgicalAccessDepth.ProcedureInProgress)) return false;
        }
        else
        {
            if (!TryComp<ChildOrganComponent>(moduleUid, out var child) || child.Parent is not { } parent ||
                !TryComp<NCSurgicalSiteComponent>(parent, out var site) || module.InstalledSlot is not { } slot ||
                !TryComp<NCMedicalBodyComponent>(body, out var medical) || !_prototypes.TryIndex(medical.Profile, out var anatomy) ||
                !_prototypes.TryIndex(anatomy.SurgicalField, out var field) || !field.CyberwareSlots.TryGetValue(slot, out var definition)) return false;
            if (site.Incisions.Count > 0)
            {
                if (!site.Incisions.Any(incision => definition.Zones.Contains(incision.TargetZone) && !incision.Sutured &&
                    incision.BoneCasingOpen && incision.EdgeSeparation >= field.MinimumOpenRetraction && incision.Depth >= definition.MinimumDepth)) return false;
            }
            else if (!site.BoneCasingOpen || !site.TissuesRetracted || site.AccessDepth is not
                (NCSurgicalAccessDepth.CavityOpened or NCSurgicalAccessDepth.EncasedStructureOpened or NCSurgicalAccessDepth.OrganExposed or
                 NCSurgicalAccessDepth.ProcedureInProgress or NCSurgicalAccessDepth.ReadyForClosure)) return false;
        }
        identity = module.StableInstanceId;
        if (TryComp<NCCyberwareAssemblyComponent>(moduleUid, out var assembly)) configurationRevision = assembly.ConfigurationRevision;
        return true;
    }
}
