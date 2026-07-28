using Content.Shared.Access;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Organizations;

[Prototype("ncOrganization")]
public sealed partial class NCOrganizationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField("organizationId", required: true)]
    public string PersistentId { get; private set; } = default!;

    public Guid OrganizationId => Guid.Parse(PersistentId);

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField]
    public ProtoId<NCPositionPrototype>? DefaultEntryPosition { get; private set; }
}

[Prototype("ncDepartment")]
public sealed partial class NCDepartmentPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField("departmentId", required: true)]
    public string PersistentId { get; private set; } = default!;

    public Guid DepartmentId => Guid.Parse(PersistentId);

    [DataField(required: true)]
    public ProtoId<NCOrganizationPrototype> Organization { get; private set; }

    [DataField(required: true)]
    public LocId Name { get; private set; }
}

[Prototype("ncPosition")]
public sealed partial class NCPositionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField("positionId", required: true)]
    public string PersistentId { get; private set; } = default!;

    public Guid PositionId => Guid.Parse(PersistentId);

    [DataField(required: true)]
    public ProtoId<NCOrganizationPrototype> Organization { get; private set; }

    [DataField]
    public ProtoId<NCDepartmentPrototype>? Department { get; private set; }

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField]
    public int RankWeight { get; private set; }

    [DataField]
    public long BaseSalary { get; private set; }

    [DataField]
    public int PayIntervalSeconds { get; private set; } = 900;

    [DataField]
    public HashSet<ProtoId<AccessLevelPrototype>> Access { get; private set; } = [];

    [DataField]
    public bool IsLeadership { get; private set; }

    [DataField]
    public bool CanHire { get; private set; }

    [DataField]
    public bool CanPromote { get; private set; }

    [DataField]
    public bool CanDemote { get; private set; }

    [DataField]
    public bool CanTransfer { get; private set; }

    [DataField]
    public bool CanSuspend { get; private set; }

    [DataField]
    public bool CanDismiss { get; private set; }

    [DataField]
    public int? MaxPromotableRankWeight { get; private set; }
}
