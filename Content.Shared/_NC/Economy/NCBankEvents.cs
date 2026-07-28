using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Economy;

[Serializable, NetSerializable]
public sealed class NCBankTransferRequest : EntityEventArgs
{
    public NetEntity Target { get; }
    public long Amount { get; }
    public string Reason { get; }
    public Guid RequestId { get; }

    public NCBankTransferRequest(NetEntity target, long amount, string reason, Guid requestId)
    {
        Target = target;
        Amount = amount;
        Reason = reason;
        RequestId = requestId;
    }
}

[Serializable, NetSerializable]
public sealed class NCBankStateEvent : EntityEventArgs
{
    public long Balance { get; }
    public string? Error { get; }

    public NCBankStateEvent(long balance, string? error = null)
    {
        Balance = balance;
        Error = error;
    }
}
