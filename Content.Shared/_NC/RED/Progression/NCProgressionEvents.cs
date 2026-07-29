// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Content.Shared._NC.Persistence;

namespace Content.Shared._NC.RED.Progression;

[Serializable, NetSerializable]
public sealed class NCCharacterStateRequest : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class NCAllocateSkillRequest : EntityEventArgs
{
    public string SkillPrototypeId { get; }
    public int TargetRank { get; }
    public Guid RequestId { get; }

    public NCAllocateSkillRequest(string skillPrototypeId, int targetRank, Guid requestId)
    {
        SkillPrototypeId = skillPrototypeId;
        TargetRank = targetRank;
        RequestId = requestId;
    }
}

[Serializable, NetSerializable]
public sealed class NCProgressionStateEvent : EntityEventArgs
{
    public int CompletedRounds { get; }
    public byte Level { get; }
    public int SpentSkillPoints { get; }
    public int TotalSkillPoints { get; }
    public IReadOnlyDictionary<string, int> Skills { get; }
    public long BankBalance { get; }
    public int PropertyCount { get; }
    public int BusinessCount { get; }
    public string CharacterName { get; }
    public string? OrganizationPrototypeId { get; }
    public string? DepartmentPrototypeId { get; }
    public string? PositionPrototypeId { get; }
    public NCPropertySummary[] Properties { get; }
    public NCBusinessSummary[] Businesses { get; }
    public NCLegalSummary[] Licenses { get; }
    public NCLegalSummary[] Documents { get; }
    public byte LifecycleStatus { get; }
    public string? Error { get; }

    public NCProgressionStateEvent(
        int completedRounds,
        byte level,
        int spentSkillPoints,
        int totalSkillPoints,
        IReadOnlyDictionary<string, int> skills,
        long bankBalance,
        int propertyCount,
        int businessCount,
        string characterName,
        string? organizationPrototypeId,
        string? departmentPrototypeId,
        string? positionPrototypeId,
        NCPropertySummary[] properties,
        NCBusinessSummary[] businesses,
        NCLegalSummary[] licenses,
        NCLegalSummary[] documents,
        byte lifecycleStatus,
        string? error = null)
    {
        CompletedRounds = completedRounds;
        Level = level;
        SpentSkillPoints = spentSkillPoints;
        TotalSkillPoints = totalSkillPoints;
        Skills = skills;
        BankBalance = bankBalance;
        PropertyCount = propertyCount;
        BusinessCount = businessCount;
        CharacterName = characterName;
        OrganizationPrototypeId = organizationPrototypeId;
        DepartmentPrototypeId = departmentPrototypeId;
        PositionPrototypeId = positionPrototypeId;
        Properties = properties;
        Businesses = businesses;
        Licenses = licenses;
        Documents = documents;
        LifecycleStatus = lifecycleStatus;
        Error = error;
    }
}
