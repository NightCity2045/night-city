// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence;

namespace Content.Shared._NC.Persistence.Components;

/// <summary>
/// Server-side runtime cache of durable character state.
/// Values are populated from the database after identity binding.
/// </summary>
[RegisterComponent]
public sealed partial class CharacterPersistentStateComponent : Component
{
    [ViewVariables]
    public ProfileId ProfileId;

    [ViewVariables]
    public bool Loaded;

    [ViewVariables]
    public string CharacterName = string.Empty;

    [ViewVariables]
    public int CompletedRounds;

    [ViewVariables]
    public byte Level = 1;

    [ViewVariables]
    public int SpentSkillPoints;

    [ViewVariables]
    public Guid? OrganizationId;

    [ViewVariables]
    public Guid? DepartmentId;

    [ViewVariables]
    public Guid? PositionId;

    [ViewVariables]
    public byte? EmploymentState;

    [ViewVariables]
    public long EmploymentVersion;

    [ViewVariables]
    public Guid? PersonalBankAccountId;

    [ViewVariables]
    public long PersonalBalance;

    [ViewVariables]
    public int PropertyCount;

    [ViewVariables]
    public int BusinessCount;

    [ViewVariables]
    public List<NCPropertySummary> Properties = [];

    [ViewVariables]
    public List<NCBusinessSummary> Businesses = [];

    [ViewVariables]
    public List<NCLegalSummary> Licenses = [];

    [ViewVariables]
    public List<NCLegalSummary> Documents = [];

    [ViewVariables]
    public byte LifecycleStatus;
}
