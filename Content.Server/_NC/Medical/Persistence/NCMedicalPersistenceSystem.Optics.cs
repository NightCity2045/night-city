// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    /// <summary>Splits a legacy pair without restoring missing eyes, healing damage or changing the old identity.</summary>
    private bool PrepareOpticMigration(InitialBodyComponent initial, ushort sourceVersion, NCMedicalStateSnapshot snapshot)
    {
        var initialOrgans = initial.Organs;
        if (sourceVersion >= NCMedicalSnapshotVersions.CyberOptics || !initialOrgans.ContainsKey("EyesRight")) return true;
        if (snapshot.Organs.ContainsKey("EyesRight") || snapshot.MissingOrganCategories.Contains("EyesRight")) return false;
        if (!snapshot.Organs.TryGetValue("Eyes", out var left))
        {
            snapshot.MissingOrganCategories.Add("EyesRight");
            return true;
        }
        var rightPrototype = left.EntityPrototype + "Right";
        if (!_prototypes.TryIndex<EntityPrototype>(rightPrototype, out var definition) ||
            !definition.TryComp<OrganComponent>(out var organ, _componentFactory) || organ.Category?.Id != "EyesRight") return false;
        var right = CloneOrgan(left);
        right.EntityPrototype = rightPrototype;
        // The old operative site/wound IDs remain on the original organ. Duplicating them would
        // create invalid cross-region references and double-count treatment history.
        if (left.SurgicalSite is { } site)
            right.SurgicalSite = new NCSurgicalSiteStateSnapshot { Contamination = site.Contamination };
        right.StableOrganId = new NCActionResolutionId(left.StableOrganId).DeriveGuid(0x45594552, 1);
        if (right.CyberwareIdentity.Length != 0) right.CyberwareIdentity = rightPrototype;
        foreach (var state in new[] { left, right })
        {
            if (state.Function is not { } function) continue;
            function.BaseCapacity *= .5f;
            function.CurrentCapacity *= .5f;
        }
        if (right.Cyberware is { } cyberware)
        {
            cyberware.EntityPrototype = rightPrototype;
            cyberware.StableInstanceId = new NCActionResolutionId(cyberware.StableInstanceId).DeriveGuid(0x45594552, 2);
            cyberware.HostCategory = null;
        }
        snapshot.Organs.Add("EyesRight", right);
        return true;
    }
}
