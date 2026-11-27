// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Bank;
using Content.Shared._NC.Corporations;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Fixers;

public enum NCFixerMissionKind : byte
{
    /// <summary>Carry a package from the objective point to a dead drop.</summary>
    Delivery,

    /// <summary>Bring a valuable item (or a target player's PDA) to the fixer's dead drop.</summary>
    Retrieval,

    /// <summary>Destroy an object at the objective point.</summary>
    Sabotage,

    /// <summary>Kill an NPC at the objective point or a target player.</summary>
    Elimination,

    /// <summary>Bring a set of goods to the fixer's dead drop.</summary>
    Collection,

    /// <summary>Beat up an NPC at the objective: down but alive. Killing them fails the job.</summary>
    Beating,

    /// <summary>Scout several points on the owner's ground: someone has to stand at each of them.</summary>
    Recon,

    /// <summary>Dirty money lands on the taker's account and has to be wired on to a shell company in time.</summary>
    Laundering,
}

public enum NCFixerPayment : byte
{
    /// <summary>Cash appears in the fixer's dead drop.</summary>
    DeadDrop,

    /// <summary>The fixer hands over cash when the taker comes back.</summary>
    InPerson,

    /// <summary>Paid to the taker's bank account.</summary>
    Transfer,
}

public enum NCMissionMarkerKind : byte
{
    /// <summary>Where a mission package, item, sabotage target or NPC target appears.</summary>
    Objective,

    /// <summary>Where a temporary dead drop appears for deliveries that do not end at the fixer.</summary>
    DeadDrop,

    /// <summary>Where a guard of the place's owner stands.</summary>
    Guard,
}

/// <summary>Who holds a place: a corporation, a gang or nobody. Markers and missions refer to it.</summary>
[Prototype("ncMissionOwner")]
public sealed partial class NCMissionOwnerPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;
}

/// <summary>Defenders spawned around a mission objective when the mission is taken.</summary>
[Prototype("ncFixerEncounter")]
public sealed partial class NCFixerEncounterPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<NCFixerEncounterEntry> Entries = new();

    /// <summary>Extra defenders per street reputation level of the taker.</summary>
    [DataField]
    public float ExtraPerLevel = 0.5f;

    /// <summary>What a guard says to someone walking into the post.</summary>
    [DataField]
    public List<LocId> Warnings = new() { "nc-mission-guard-warning-1", "nc-mission-guard-warning-2" };

    /// <summary>What a guard shouts when the group turns hostile.</summary>
    [DataField]
    public List<LocId> Alarms = new() { "nc-mission-guard-alarm-1", "nc-mission-guard-alarm-2" };
}

[DataDefinition]
public sealed partial class NCFixerEncounterEntry
{
    [DataField(required: true)]
    public EntProtoId Mob;

    [DataField]
    public int Min = 1;

    [DataField]
    public int Max = 1;
}

/// <summary>A fixer: an NPC players talk to for missions.</summary>
[Prototype("ncFixer")]
public sealed partial class NCFixerPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public LocId? Description;

    /// <summary>Lines the fixer opens a conversation with; one is picked at random.</summary>
    [DataField]
    public List<LocId> Greetings = new();

    [DataField]
    public NCFixerPayment Payment = NCFixerPayment.DeadDrop;

    /// <summary>How many missions the fixer offers at once.</summary>
    [DataField]
    public int Offers = 3;

    [DataField]
    public TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
}

/// <summary>One mission of a fixer's pool.</summary>
[Prototype("ncFixerMission")]
public sealed partial class NCFixerMissionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<NCFixerPrototype> Fixer;

    [DataField(required: true)]
    public NCFixerMissionKind Kind;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public LocId? Description;

    [DataField]
    public float Weight = 1f;

    /// <summary>Street reputation the taker needs.</summary>
    [DataField]
    public int MinimumReputation;

    [DataField(required: true)]
    public long Reward;

    [DataField]
    public int ReputationGain = 2;

    [DataField]
    public int ReputationLoss = 3;

    [DataField]
    public TimeSpan TimeLimit = TimeSpan.FromMinutes(30);

    /// <summary>Overrides how the fixer pays for this mission.</summary>
    [DataField]
    public NCFixerPayment? Payment;

    /// <summary>Whose place the objective is in; picks the objective marker.</summary>
    [DataField]
    public ProtoId<NCMissionOwnerPrototype>? Owner;

    /// <summary>Package, valuable item, sabotage target or NPC target spawned at the objective.</summary>
    [DataField]
    public EntProtoId? Item;

    /// <summary>
    /// Retrieval only: a terminal spawned at the objective instead of the item. Hacking it produces the item.
    /// </summary>
    [DataField]
    public EntProtoId? Terminal;

    /// <summary>Deliveries only: the fixer hands the item to the taker instead of it waiting at an objective.</summary>
    [DataField]
    public bool ItemFromFixer;

    /// <summary>The job fails the moment its defenders turn hostile: talks, not a war.</summary>
    [DataField]
    public bool FailOnHostile;

    /// <summary>Recon only: how many points of the owner's ground to scout.</summary>
    [DataField]
    public int ReconPoints = 3;

    /// <summary>Laundering only: the dirty money put on the taker's account.</summary>
    [DataField]
    public long LaunderAmount;

    /// <summary>Laundering only: the shell company account the money has to reach.</summary>
    [DataField]
    public ProtoId<NCBankDepartmentPrototype>? LaunderAccount;

    /// <summary>Deliveries only: true sends the package to a dead drop marker instead of the fixer's drop.</summary>
    [DataField]
    public bool DropAtMarker;

    /// <summary>Elimination or retrieval of a player: the target works in one of these departments.</summary>
    [DataField]
    public List<ProtoId<DepartmentPrototype>> TargetDepartments = new();

    /// <summary>Collection only: what to bring.</summary>
    [DataField]
    public List<NCCorpContractRequirement> Requirements = new();

    /// <summary>Defenders spawned at guard markers in the objective's building when the mission is taken.</summary>
    [DataField]
    public ProtoId<NCFixerEncounterPrototype>? Encounter;
}

/// <summary>Street reputation levels shared by all fixers. The prototype with id Default is used.</summary>
[Prototype("ncStreetReputation")]
public sealed partial class NCStreetReputationPrototype : IPrototype
{
    public const string DefaultId = "Default";

    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Reputation needed for each level, ascending; level 0 starts at 0.</summary>
    [DataField(required: true)]
    public List<int> Thresholds = new();

    [DataField(required: true)]
    public List<LocId> LevelNames = new();

    public int Level(int reputation)
    {
        var level = 0;
        for (var i = 0; i < Thresholds.Count; i++)
        {
            if (reputation >= Thresholds[i])
                level = i;
        }

        return level;
    }
}
