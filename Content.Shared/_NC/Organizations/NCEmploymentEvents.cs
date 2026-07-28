using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Organizations;

[Serializable, NetSerializable]
public enum NCEmploymentActionType : byte
{
    Hire,
    Promote,
    Demote,
    Transfer,
    Suspend,
    Reinstate,
    Dismiss,
}

[Serializable, NetSerializable]
public sealed class NCEmploymentActionRequest : EntityEventArgs
{
    public NetEntity Target { get; }
    public NCEmploymentActionType Action { get; }
    public string? OrganizationPrototypeId { get; }
    public string? PositionPrototypeId { get; }
    public string Reason { get; }
    public Guid RequestId { get; }

    public NCEmploymentActionRequest(
        NetEntity target,
        NCEmploymentActionType action,
        string? organizationPrototypeId,
        string? positionPrototypeId,
        string reason,
        Guid requestId)
    {
        Target = target;
        Action = action;
        OrganizationPrototypeId = organizationPrototypeId;
        PositionPrototypeId = positionPrototypeId;
        Reason = reason;
        RequestId = requestId;
    }
}

[Serializable, NetSerializable]
public sealed class NCEmploymentActionResponse : EntityEventArgs
{
    public bool Success { get; }
    public string? Error { get; }

    public NCEmploymentActionResponse(bool success, string? error)
    {
        Success = success;
        Error = error;
    }
}
