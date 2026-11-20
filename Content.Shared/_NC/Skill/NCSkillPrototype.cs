// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._NC.Skill;

[Prototype("ncSkillTree")]
public sealed partial class NCSkillTreePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name { get; private set; }
}

[Prototype("ncSkill")]
public sealed partial class NCSkillPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCSkillTreePrototype> Tree { get; private set; }
    [DataField(required: true)] public LocId Name { get; private set; }
    [DataField(required: true)] public LocId Description { get; private set; }
    [DataField(required: true)] public SpriteSpecifier Icon { get; private set; } = default!;
    [DataField] public Vector2 Position { get; private set; }
    [DataField] public ProtoId<NCSkillPrototype>[] Prerequisites { get; private set; } = [];
    [DataField] public ProtoId<NCSkillPrototype>[][] AnyPrerequisites { get; private set; } = [];

    public bool MeetsPrerequisites(IReadOnlySet<string> learned) =>
        Prerequisites.All(skill => learned.Contains(skill.Id)) &&
        AnyPrerequisites.All(group => group.Any(skill => learned.Contains(skill.Id)));
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCRequiredSkillComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public ProtoId<NCSkillPrototype> Skill;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class NCSkillPermissionsComponent : Component
{
    public override bool SendOnlyToOwner => true;

    [AutoNetworkedField]
    public int Experience = -1;

    [AutoNetworkedField]
    public string[] Learned = [];
}
