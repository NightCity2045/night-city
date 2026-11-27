// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Bank;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Corporations;

/// <summary>
/// A corporation that sells goods through contracts: its field department does the work and receives
/// <see cref="DepartmentShare"/> of every reward, the rest goes to the corporation account.
/// </summary>
[Prototype("ncCorpSales")]
public sealed partial class NCCorpSalesPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    /// <summary>Bank account of the field department; its employees may fulfil contracts.</summary>
    [DataField(required: true)]
    public ProtoId<NCBankDepartmentPrototype> Department;

    /// <summary>Bank account of the corporation itself.</summary>
    [DataField(required: true)]
    public ProtoId<NCBankDepartmentPrototype> Corporation;

    /// <summary>How many contracts are open at once.</summary>
    [DataField]
    public int OpenContracts = 4;

    [DataField]
    public float DepartmentShare = 0.5f;
}

/// <summary>
/// One contract of the random pool. It has no deadline and is only fulfilled in full.
/// </summary>
[Prototype("ncCorpContract")]
public sealed partial class NCCorpContractPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<NCCorpSalesPrototype> Corporation;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public LocId? Description;

    [DataField(required: true)]
    public long Reward;

    /// <summary>Relative chance to be drawn into the open contracts.</summary>
    [DataField]
    public float Weight = 1f;

    [DataField(required: true)]
    public List<NCCorpContractRequirement> Requirements = new();
}

/// <summary>Something a contract asks for.</summary>
[ImplicitDataDefinitionForInheritors]
public abstract partial class NCCorpContractRequirement
{
    /// <summary>Items (stacks count each unit) or, for drugs, doses.</summary>
    [DataField(required: true)]
    public int Amount;
}

/// <summary>Items of an exact prototype: weapons, armor, implants, food, harvested produce.</summary>
public sealed partial class NCCorpItemRequirement : NCCorpContractRequirement
{
    [DataField(required: true)]
    public EntProtoId Item;
}

/// <summary>
/// Finished implants of a model: every required part fitted and, by default, the assembly passed its bench test.
/// <see cref="Item"/> is the model's base prototype, so factory implants and frames assembled in game both count.
/// </summary>
public sealed partial class NCCorpImplantRequirement : NCCorpContractRequirement
{
    [DataField(required: true)]
    public EntProtoId Item;

    [DataField]
    public bool Tested = true;
}

/// <summary>Doses of a pharmacology formula, packaged in batch containers.</summary>
public sealed partial class NCCorpDrugRequirement : NCCorpContractRequirement
{
    [DataField(required: true)]
    public ProtoId<NCPharmaFormulaPrototype> Formula;

    [DataField]
    public NCPharmaGrade MinimumGrade = NCPharmaGrade.Medical;

    /// <summary>Only batches that passed full quality control count.</summary>
    [DataField]
    public bool Certified = true;
}
