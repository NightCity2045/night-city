// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Persistence;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    private bool PrepareArmMigration(ushort version, NCMedicalStateSnapshot snapshot)
    {
        if (version >= NCMedicalSnapshotVersions.ModularArms) return true;
        foreach (var module in snapshot.CyberwareModules)
        {
            if (module.Profile.Id is not ("NCToolHand" or "NCMantisBlade")) continue;
            if (!_prototypes.TryIndex<EntityPrototype>(module.EntityPrototype, out var entity) ||
                !entity.TryComp<NCCyberwareComponent>(out var hardware, _componentFactory)) return false;
            module.Assembly ??= _cyberware.CreateDefaultAssemblySnapshot(module.Profile, module.StableInstanceId, hardware.QualityParts);
            if (module.Assembly == null) return false;
            module.Assembly.ValidatedRevision = module.CurrentIntegrity > 0 ? module.Assembly.ConfigurationRevision : null;
            module.FactoryPrepared = module.CurrentIntegrity > 0;
        }
        return true;
    }
}
