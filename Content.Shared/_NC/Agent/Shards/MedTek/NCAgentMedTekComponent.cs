// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.MedTek;

/// <summary>
/// Data-only medical scan history stored by one physical MEDTEK shard.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentMedTekComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<NCAgentMedicalScan> History = [];

    [DataField, AutoNetworkedField]
    public int MaximumHistory = 12;
}

[Serializable, NetSerializable, DataRecord]
public sealed partial class NCAgentMedicalScan
{
    public readonly Guid Id;
    public readonly string TargetName;
    public readonly DateTime ScannedAt;
    public readonly float Temperature;
    public readonly float BloodLevel;
    public readonly bool Bleeding;
    public readonly bool Unrevivable;
    public readonly float TotalDamage;
    public readonly List<NCAgentMedicalDamage> Damage;

    public NCAgentMedicalScan(
        Guid id,
        string targetName,
        DateTime scannedAt,
        float temperature,
        float bloodLevel,
        bool bleeding,
        bool unrevivable,
        float totalDamage,
        List<NCAgentMedicalDamage> damage)
    {
        Id = id;
        TargetName = targetName;
        ScannedAt = scannedAt;
        Temperature = temperature;
        BloodLevel = bloodLevel;
        Bleeding = bleeding;
        Unrevivable = unrevivable;
        TotalDamage = totalDamage;
        Damage = damage;
    }
}

[Serializable, NetSerializable, DataRecord]
public sealed partial class NCAgentMedicalDamage(string type, float amount)
{
    public readonly string Type = type;
    public readonly float Amount = amount;
}
