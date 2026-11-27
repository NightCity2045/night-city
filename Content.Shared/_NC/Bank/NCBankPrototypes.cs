// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.Roles;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Bank;

/// <summary>
/// Global bank settings. The bank uses the prototype with id <see cref="DefaultId"/>.
/// </summary>
[Prototype("ncBankConfiguration")]
public sealed partial class NCBankConfigurationPrototype : IPrototype
{
    public const string DefaultId = "Default";

    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Round time between paydays.</summary>
    [DataField]
    public TimeSpan PaydayInterval = TimeSpan.FromMinutes(30);

    /// <summary>Fee rate of personal accounts, in basis points (1/100 of a percent), until an admin changes it.</summary>
    [DataField]
    public int ResidentFeeBasisPoints = 4000;

    /// <summary>Balance of a new character account.</summary>
    [DataField]
    public long CharacterOpeningBalance = 1000;

    /// <summary>Cash handed to a character once, when its account is opened.</summary>
    [DataField]
    public int CharacterOpeningCash = 200;

    /// <summary>Stack type that counts as cash.</summary>
    [DataField]
    public ProtoId<StackPrototype> CurrencyStack = "Credit";

    /// <summary>Largest single deposit, withdrawal or transfer.</summary>
    [DataField]
    public long MaximumOperation = 1_000_000;
}

/// <summary>
/// Bank settings of one organization account: a department (the prototype id is the department id) or an account
/// with no department, such as a corporation's headquarters.
/// </summary>
[Prototype("ncBankDepartment")]
public sealed partial class NCBankDepartmentPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>The department whose jobs this account pays. Null for accounts that employ nobody.</summary>
    [DataField]
    public ProtoId<DepartmentPrototype>? Department;

    /// <summary>Display name; defaults to the department name.</summary>
    [DataField]
    public LocId? Name;

    /// <summary>Jobs allowed to manage the account from a department terminal.</summary>
    [DataField]
    public List<ProtoId<JobPrototype>> Managers = new();

    /// <summary>Fee rate of the department account, in basis points, until an admin changes it.</summary>
    [DataField]
    public int FeeBasisPoints = 1000;

    /// <summary>Balance of the account when it is first created.</summary>
    [DataField]
    public long OpeningBalance;

    /// <summary>Salary per payday of each job, until the department manager changes it.</summary>
    [DataField]
    public Dictionary<ProtoId<JobPrototype>, int> Salaries = new();
}
