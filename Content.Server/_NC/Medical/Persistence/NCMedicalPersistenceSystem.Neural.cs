// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    /// <summary>Version 18 adds previously unmodelled anatomy; it never resurrects a missing parent region.</summary>
    private bool PrepareNeuralMigration(InitialBodyComponent initial, ushort sourceVersion, NCMedicalStateSnapshot snapshot)
    {
        if (!TryBuildParentMap(initial, out var parents))
            return false;
        var initialOrgans = initial.Organs;
        foreach (ProtoId<OrganCategoryPrototype> category in new ProtoId<OrganCategoryPrototype>[] { "Spine", "VertebralColumn" })
        {
            if (sourceVersion >= NCMedicalSnapshotVersions.NeuralFoundations ||
                !initialOrgans.TryGetValue(category, out var prototype))
                continue;
            if (snapshot.Organs.ContainsKey(category) || snapshot.MissingOrganCategories.Contains(category) ||
                !parents.TryGetValue(category, out var parent))
                return false;
            if (!snapshot.Organs.TryGetValue(parent, out var parentState))
            {
                snapshot.MissingOrganCategories.Add(category);
                continue;
            }
            if (!_prototypes.TryIndex<EntityPrototype>(prototype, out var definition) ||
                !definition.TryComp<NCOrganIntegrityComponent>(out var integrity, _componentFactory))
                return false;
            var state = new NCOrganMedicalStateSnapshot
            {
                EntityPrototype = prototype,
                StableOrganId = Guid.NewGuid(),
                DonorKind = parentState.DonorKind,
                DonorKey = parentState.DonorKey,
                CyberwareIdentity = integrity.Material == NCAnatomicalMaterialKind.Organic ? string.Empty : prototype,
                Integrity = new NCOrganIntegrityStateSnapshot
                {
                    MaxIntegrity = integrity.MaxIntegrity, CurrentIntegrity = integrity.CurrentIntegrity,
                    FunctionalEfficiency = integrity.FunctionalEfficiency, Recoverable = integrity.Recoverable,
                    Necrosis = integrity.Necrosis, Material = integrity.Material,
                },
                SurgicalSite = new NCSurgicalSiteStateSnapshot(),
            };
            if (definition.TryComp<NCNerveComponent>(out var nerve, _componentFactory))
                state.Nerve = new NCNerveStateSnapshot
                {
                    NeuralIntegrity = nerve.NeuralIntegrity, Sensitivity = nerve.Sensitivity,
                    MotorFunction = nerve.MotorFunction, CyberprostheticInterface = nerve.CyberprostheticInterface,
                };
            if (definition.TryComp<NCBoneComponent>(out var bone, _componentFactory))
                state.Bone = new NCBoneStateSnapshot
                {
                    State = bone.State, Stability = bone.Stability, Displacement = bone.Displacement,
                    OpenFracture = bone.OpenFracture, SecondaryDamageRisk = bone.SecondaryDamageRisk,
                    RequiresFixation = bone.RequiresFixation, Splinted = bone.Splinted, SplintedAt = bone.SplintedAt,
                };
            snapshot.Organs.Add(category, state);
        }
        foreach (var module in snapshot.CyberwareModules)
        {
            if (module.Slot?.Id != "NCSlotMotorBurst" || module.HostCategory?.Id != "Head")
                continue;
            if (!snapshot.Organs.ContainsKey("Spine"))
                continue; // A disconnected legacy biopod retains its old hardware; no anatomy is invented.
            // The physical item, serial, condition and cooldown are retained; only the semantic host changes.
            module.HostCategory = "Spine";
        }
        return true;
    }
}
