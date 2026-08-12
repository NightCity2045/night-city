// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Content.Shared._NC.Medical.Persistence;

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// Marks a human body whose anatomy, physiology, consciousness and death are owned by NC medicine.
/// The component intentionally has no compatibility mode: human medicine is always authoritative.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(NCAnatomySystem), typeof(NCMedicalBodySystem))]
public sealed partial class NCMedicalBodyComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public ProtoId<NCAnatomyProfilePrototype> Profile;

    /// <summary>
    /// Version used by future persistent medical snapshots and migrations.
    /// </summary>
    [DataField, AutoNetworkedField]
    public ushort SnapshotVersion = NCMedicalSnapshotVersions.Current;

    /// <summary>
    /// Runtime-only invalidation domains. Systems clear only the domains they recalculate.
    /// </summary>
    [ViewVariables]
    public NCMedicalDirtyDomains DirtyDomains = NCMedicalDirtyDomains.All;

    /// <summary>
    /// Increments after an authoritative medical mutation. Detailed state is sent through private snapshots.
    /// </summary>
    [ViewVariables]
    public uint MedicalRevision;
}

[Flags]
public enum NCMedicalDirtyDomains : ushort
{
    None = 0,
    Anatomy = 1 << 0,
    Wounds = 1 << 1,
    Vascular = 1 << 2,
    Pain = 1 << 3,
    OrganFunction = 1 << 4,
    Consciousness = 1 << 5,
    Diagnostics = 1 << 6,
    Persistence = 1 << 7,
    All = Anatomy | Wounds | Vascular | Pain | OrganFunction | Consciousness | Diagnostics | Persistence,
}
