// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Movement.Systems;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private MovementSpeedModifierSystem _skeletalMovement = default!;
    // Requirements are one layer deep: evaluating a graft never enters another support requirement.
    private readonly List<float> _supportEfficiencies = new(2);

    private float ResolveSupportFactor(EntityUid uid, NCCyberwareComponent module, NCCyberwarePrototype profile,
        TimeSpan now, IReadOnlyList<EntityUid>? modules)
    {
        module.AvailableSupports = 0;
        module.SupportReady = true;
        if (profile.RequiredSupportProfile is not { } required)
            return 1f;
        module.SupportReady = false;
        if (!TryResolveOwningBody(uid, out var body))
            return 0f;
        modules ??= CollectCyberware(body);
        _supportEfficiencies.Clear();
        foreach (var candidate in modules)
        {
            if (candidate == uid || TerminatingOrDeleted(candidate) ||
                !TryComp<NCCyberwareComponent>(candidate, out var support) || support.Profile != required)
                continue;
            var definition = _prototypes.Index(support.Profile);
            if (definition.RequiredSupportProfile != null) continue;
            UpdateNeuralState(support, definition, now);
            UpdateOperational(candidate, support, definition, now, modules);
            if (support.FunctionalEfficiency <= 0f) continue;
            _supportEfficiencies.Add(support.FunctionalEfficiency);
        }
        module.AvailableSupports = _supportEfficiencies.Count;
        var efficiency = NCSkeletalSupportMath.SupportEfficiency(_supportEfficiencies, profile.RequiredSupportCount);
        module.SupportReady = efficiency > 0f;
        return efficiency;
    }

    private void RefreshSkeletalSupport(EntityUid body, IReadOnlyList<EntityUid> modules)
    {
        var allowance = 0f;
        var boneMultiplier = 1f;
        var hasEnhancement = false;
        foreach (var uid in modules)
        {
            var module = Comp<NCCyberwareComponent>(uid);
            var profile = _prototypes.Index(module.Profile);
            if (!profile.SkeletalEnhancement) continue;
            hasEnhancement = true;
            var efficiency = module.FunctionalEfficiency;
            if (profile.RequiresPower)
                efficiency *= module.CrisisBlocksActiveModes ? 0f : module.CrisisEfficiency;
            allowance = MathF.Max(allowance, profile.HardsuitPenaltyAllowance * efficiency);
            boneMultiplier = MathF.Min(boneMultiplier, 1f - (1f - profile.BoneTraumaMultiplier) * efficiency);
        }
        if (!TryComp<NCSkeletalSupportBodyComponent>(body, out var state))
        {
            if (!hasEnhancement) return;
            state = EnsureComp<NCSkeletalSupportBodyComponent>(body);
        }
        state.BoneTraumaMultiplier = Math.Clamp(boneMultiplier, 0f, 1f);
        allowance = Math.Clamp(allowance, 0f, 1f);
        if (MathHelper.CloseTo(state.HardsuitPenaltyAllowance, allowance)) return;
        state.HardsuitPenaltyAllowance = allowance;
        Dirty(body, state);
        _skeletalMovement.RefreshMovementSpeedModifiers(body);
    }
}
