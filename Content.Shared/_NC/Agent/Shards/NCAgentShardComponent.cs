// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

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
