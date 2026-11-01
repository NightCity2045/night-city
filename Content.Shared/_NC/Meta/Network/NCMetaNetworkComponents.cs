// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.Damage;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.Network;

[RegisterComponent]
public sealed partial class NCMetaMessagePortComponent : Component
{
    [DataField] public string Address = string.Empty;
    [DataField] public List<string> AllowedPeers = [];
    [DataField] public List<string> AllowedTopics = [];
    [DataField] public bool AllowBroadcast;
    [DataField] public int MaximumRecipients = 16;
    [DataField] public int QueueMessages = 16;
    [DataField] public int QueueBytes = 32768;
    [DataField] public int LatencyMilliseconds = 50;
    [DataField] public List<string> GatewayNetworks = [];
}

[RegisterComponent]
public sealed partial class NCMetaNetworkCableComponent : Component
{
    [DataField] public string DeviceAId = string.Empty;
    [DataField] public string DeviceBId = string.Empty;
    public EntityUid? PortA;
    public EntityUid? PortB;
    [DataField] public float MaximumLength = 8;
}

[RegisterComponent]
public sealed partial class NCMetaRadioInterferenceComponent : Component
{
    [DataField] public bool Enabled = true;
    [DataField] public float Radius = 6;
    [DataField] public int Strength = 1;
    [DataField] public string Channel = "meta-local";
}

[Prototype("ncMetaRadio")]
public sealed partial class NCMetaRadioProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public string Channel = "meta-local";
    [DataField] public float MaximumRange = 8;
    [DataField] public int MaximumInterference;
}

[RegisterComponent]
public sealed partial class NCMetaNetAccessPointComponent : Component
{
    [DataField] public string ArchitectureId = string.Empty;
    [DataField] public List<string> EntryNodes = [];
    [DataField] public bool PublicEntry = true;
}

[RegisterComponent]
public sealed partial class NCMetaCyberdeckComponent : Component
{
    [DataField] public ProtoId<NCMetaNetToolProfilePrototype> ToolProfile = "NCMetaNetProbe";
    [DataField] public bool NeuralInterface;
}

[RegisterComponent]
public sealed partial class NCMetaNetNodeComponent : Component
{
    [DataField] public string ArchitectureId = string.Empty;
    [DataField] public string NodeId = string.Empty;
    [DataField] public LocId Label = "nc-meta-net-node";
    [DataField] public NCMetaNetNodeKind Kind;
    [DataField] public List<string> Neighbors = [];
    [DataField] public ProtoId<NCMetaNetDefenseProfilePrototype> Defense = "NCMetaNetUnprotected";
    [DataField] public string DataPath = string.Empty;
    [DataField] public string ControlledDeviceId = string.Empty;
}

[Prototype("ncMetaNetTool")]
public sealed partial class NCMetaNetToolProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int Rating = 2;
    [DataField] public List<NCMetaNetNodeKind> CompatibleKinds = new() { NCMetaNetNodeKind.Data, NCMetaNetNodeKind.Control, NCMetaNetNodeKind.Defense };
    [DataField] public int DurationMilliseconds = 1000;
    [DataField] public int Cost = 1;
    [DataField] public int Budget = 10;
    [DataField] public int CooldownMilliseconds = 1000;
}

[Prototype("ncMetaNetDefense")]
public sealed partial class NCMetaNetDefenseProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int Rating;
    [DataField] public int TraceOnSuccess = 1;
    [DataField] public int TraceOnFailure = 3;
    [DataField] public int IceThreshold = 6;
    [DataField] public int BlackIceThreshold = 9;
    [DataField] public int ThreatIntervalMilliseconds = 1000;
    [DataField] public DamageSpecifier BlackIceDamage = new();
}

[Serializable, NetSerializable]
public enum NCMetaNetNodeKind : byte { Data, Control, Defense }

[Serializable, NetSerializable]
public enum NCMetaNetUiKey : byte { Console }

[Serializable, NetSerializable]
public enum NCMetaNetOperation : byte { Refresh, Connect, Disconnect, Scan, Probe, Read, AcquireControl, Defend, Activate, Deactivate, CopySoftware }

[Serializable, NetSerializable]
public sealed record NCMetaNetNodeView(string Id, string Label, NCMetaNetNodeKind Kind, bool Probed, bool Accessible);

[Serializable, NetSerializable]
public sealed class NCMetaNetState(Guid sessionId, long revision, string stateKey, int trace, int budget,
    NCMetaNetNodeView[] nodes, string statusKey, bool actionPending, string preview = "") : BoundUserInterfaceState
{
    public readonly Guid SessionId = sessionId;
    public readonly long Revision = revision;
    public readonly string StateKey = stateKey;
    public readonly int Trace = trace;
    public readonly int Budget = budget;
    public readonly NCMetaNetNodeView[] Nodes = nodes;
    public readonly string StatusKey = statusKey;
    public readonly bool ActionPending = actionPending;
    public readonly string Preview = preview;
}

[Serializable, NetSerializable]
public sealed class NCMetaNetRequest(NCMetaNetOperation operation, Guid sessionId, long revision, string nodeId = "", NetEntity? accessPoint = null) : BoundUserInterfaceMessage
{
    public readonly NCMetaNetOperation Operation = operation;
    public readonly Guid SessionId = sessionId;
    public readonly long Revision = revision;
    public readonly string NodeId = nodeId;
    public readonly NetEntity? AccessPoint = accessPoint;
}

[Serializable, NetSerializable]
public sealed class NCMetaNetUpdate(NCMetaNetState state) : BoundUserInterfaceMessage
{
    public readonly NCMetaNetState State = state;
}
