// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Content.Shared.Access;

namespace Content.Shared._NC.Agent.Shards;

/// <summary>
/// Data-driven presentation and resource metadata for a program shown by the Agent SHARDS app.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentShardComponent : Component
{
    [DataField, AutoNetworkedField]
    public string DisplayName = "SHARD";

    [DataField, AutoNetworkedField]
    public string Description = string.Empty;

    [DataField, AutoNetworkedField]
    public int Size = 1;

    [DataField, AutoNetworkedField]
    public NCAgentShardCategory Category = NCAgentShardCategory.Utility;

    [DataField, AutoNetworkedField]
    public List<NCAgentShardPermission> Permissions = [];

    /// <summary>
    /// Any one of these access tags on the Agent's inserted VIN card authorizes the program.
    /// An empty list makes the program public.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<ProtoId<AccessLevelPrototype>> RequiredAccess = [];

    /// <summary>
    /// Allows the program to open so its server system can validate persistent employment or another authoritative
    /// policy that cannot be predicted from the inserted VIN card.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool ServerAuthorized;

    /// <summary>
    /// Short presentation-only name for the authorization policy shown in SHARDS.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string AccessLabel = "PUBLIC";
}

/// <summary>
/// Configures the finite program memory of one physical Agent.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentShardStorageComponent : Component
{
    [DataField, AutoNetworkedField]
    public int Capacity = 16;

    [DataField, AutoNetworkedField]
    public int DefaultProgramSize = 1;
}

[Serializable, NetSerializable]
public enum NCAgentShardCategory : byte
{
    Personal,
    Network,
    Utility,
    Security,
    Medical,
    Legacy,
}

[Serializable, NetSerializable]
public enum NCAgentShardPermission : byte
{
    LocalStorage,
    NetworkRead,
    LocationRead,
    VinRead,
    DeviceControl,
}

/// <summary>
/// Raised before an Agent opens or relays a message to a protected shard.
/// Server authority and client prediction evaluate the same VIN policy.
/// </summary>
[ByRefEvent]
public record struct NCAgentShardAuthorizationAttemptEvent(EntityUid Loader, EntityUid Program, bool Cancelled = false);
