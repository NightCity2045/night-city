using Content.Shared._NC.Identity;

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
    public Guid? PersonalBankAccountId;

    [ViewVariables]
    public long PersonalBalance;

    [ViewVariables]
    public int PropertyCount;

    [ViewVariables]
    public int BusinessCount;

    [ViewVariables]
    public byte LifecycleStatus;
}
