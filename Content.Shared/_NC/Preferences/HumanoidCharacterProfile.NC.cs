// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared.Preferences;

public sealed partial class HumanoidCharacterProfile
{
    /// <summary>
    /// The department selected for the character's initial employment. A null value means the character remains a resident.
    /// Server-side employment remains authoritative and is stored separately.
    /// </summary>
    [DataField]
    public ProtoId<DepartmentPrototype>? NCDepartmentPreference { get; private set; }

    public HumanoidCharacterProfile WithNCDepartmentPreference(ProtoId<DepartmentPrototype>? department)
    {
        return new HumanoidCharacterProfile(this)
        {
            NCDepartmentPreference = department,
        };
    }

    /// <summary>
    /// Removes stale or server-only department IDs sent by a client.
    /// </summary>
    private void EnsureNCDepartmentPreferenceValid(IPrototypeManager prototypes)
    {
        if (NCDepartmentPreference is not { } department ||
            !prototypes.TryIndex(department, out var prototype) ||
            !prototype.NCSelectable)
        {
            NCDepartmentPreference = null;
        }
    }
}
