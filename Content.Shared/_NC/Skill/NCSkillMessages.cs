// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Skill;

[Serializable, NetSerializable]
public sealed class NCSkillsState(int experience, string[] learned) : EuiStateBase
{
    public int Experience { get; } = experience;
    public string[] Learned { get; } = learned;
}

[Serializable, NetSerializable]
public sealed class NCLearnSkillMessage(string skill) : EuiMessageBase
{
    public string Skill { get; } = skill;
}

[Serializable, NetSerializable]
public sealed class NCLearnSkillResult(bool success) : EuiMessageBase
{
    public bool Success { get; } = success;
}

[Serializable, NetSerializable]
public sealed record NCSkillNode(string Id, Vector2 Position, string[] Prerequisites, string[][] AnyPrerequisites);

[Serializable, NetSerializable]
public sealed class NCSkillEditorMessage(string tree, NCSkillNode[]? nodes) : EuiMessageBase
{
    public string Tree { get; } = tree;
    public NCSkillNode[]? Nodes { get; } = nodes;
}

[Serializable, NetSerializable]
public sealed class NCSkillEditorResult(string tree, NCSkillNode[]? nodes, LocId status) : EuiMessageBase
{
    public string Tree { get; } = tree;
    public NCSkillNode[]? Nodes { get; } = nodes;
    public LocId Status { get; } = status;
}
