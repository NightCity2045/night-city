// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Movement.Systems;
using Content.Shared.Tag;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware;

/// <summary>Only the movement allowance is replicated. It is derived from actual server-owned implants.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), UnsavedComponent]
public sealed partial class NCSkeletalSupportBodyComponent : Component
{
    [AutoNetworkedField]
    public float HardsuitPenaltyAllowance;

    [ViewVariables]
    public float BoneTraumaMultiplier = 1f;
}

public static class NCSkeletalSupportMath
{
    public static float MotorLoad(float penalty, float allowance, bool ready)
    {
        if (!ready || !float.IsFinite(penalty) || !float.IsFinite(allowance) || allowance <= 0f) return 0f;
        return Math.Clamp(penalty / allowance, 0f, 1f);
    }

    public static float Compensate(float modifier, float allowance)
    {
        if (!float.IsFinite(modifier)) return 1f;
        if (modifier >= 1f) return modifier;
        var budget = float.IsFinite(allowance) ? Math.Clamp(allowance, 0f, 1f) : 0f;
        return Math.Clamp(modifier + budget, 0f, 1f);
    }

    /// <summary>The weakest of the best required supports bounds operation. Duplicate grafts never add speed.</summary>
    public static float SupportEfficiency(IReadOnlyList<float> efficiencies, int required)
    {
        if (required is < 1 or > 2) return 0f;
        var best = 0f;
        var second = 0f;
        for (var i = 0; i < efficiencies.Count; i++)
        {
            var value = efficiencies[i];
            if (!float.IsFinite(value) || value <= 0f) continue;
            value = Math.Clamp(value, 0f, 1f);
            if (value > best) { second = best; best = value; }
            else if (value > second) second = value;
        }
        return required == 1 ? best : second;
    }

}

public sealed partial class NCSkeletalSupportSystem : EntitySystem
{
    private static readonly ProtoId<TagPrototype> HardsuitTag = "Hardsuit";
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCSkeletalSupportBodyComponent, AfterAutoHandleStateEvent>(OnState);
    }

    private void OnState(Entity<NCSkeletalSupportBodyComponent> body, ref AfterAutoHandleStateEvent args) =>
        _movement.RefreshMovementSpeedModifiers(body.Owner);

    public (float Walk, float Sprint) GetClothingModifiers(EntityUid wearer, EntityUid clothing, float walk, float sprint)
    {
        if (!TryComp<NCSkeletalSupportBodyComponent>(wearer, out var support) ||
            support.HardsuitPenaltyAllowance <= 0f || !_tags.HasTag(clothing, HardsuitTag))
            return (walk, sprint);
        return (NCSkeletalSupportMath.Compensate(walk, support.HardsuitPenaltyAllowance),
            NCSkeletalSupportMath.Compensate(sprint, support.HardsuitPenaltyAllowance));
    }
}
