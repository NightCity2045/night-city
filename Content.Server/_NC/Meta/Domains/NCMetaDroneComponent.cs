// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Domains;

public enum NCMetaDroneMode { Manual, Assistant, Autonomous, Maintenance, Emergency }

[RegisterComponent]
public sealed partial class NCMetaDroneComponent : Component
{
    /// <summary>Explicit mission-map markers, never an arbitrary world/entity lookup.</summary>
    [DataField] public Dictionary<string, EntityUid> Waypoints = new(StringComparer.Ordinal);
    [DataField] public bool AutonomousEnabled = true;
    [DataField] public float MaximumNavigationSeconds = 60;
    [DataField] public int MaximumReplans = 2;
    [DataField] public float ReplanCooldownSeconds = 1;
    public NCMetaDroneMode Mode = NCMetaDroneMode.Autonomous;
    public EntityUid? Operator;
    public TimeSpan AssistantUntil;
    public ulong ControlEpoch = 1;
    public ulong TaskControlEpoch;
    public int Outcome;
    public int Replans;
    public TimeSpan NextReplan;
    public bool ManualOverride;
    public NCMetaRuntimeInstance? Controller;
    public EntityUid? Target;
    public long RequestId;
    public uint IssuedTick;
    public TimeSpan Deadline;
    public TimeSpan NextCommand;
    public TimeSpan NextActuatorCommand;
    public int PendingPhysicalOperations;
    public Dictionary<(Guid Runtime, ulong Token), (string Name, EntityUid Target)> References = [];
}
