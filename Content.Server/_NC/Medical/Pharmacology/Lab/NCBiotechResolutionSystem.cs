// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Answers the quality requests of professional laboratory procedures from the actor's real body: how well the
/// hands grip, how well they see, how steady their movement is, how awake they are. Nothing here is a skill or
/// a roll. A future skill system answers the same request and multiplies its own factor on top.
/// </summary>
public sealed partial class NCBiotechResolutionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCBiotechActionResolveEvent>(OnResolve);
    }

    private void OnResolve(ref NCBiotechActionResolveEvent args)
    {
        if (!_prototypes.TryIndex(args.Request.Procedure, out var procedure))
            return;

        args.Quality *= NCBiotechQualityMath.Resolve(procedure, Measure(args.Request.Actor));
    }

    /// <summary>The actor's state for a procedure. A being without a medical body is treated as fully able.</summary>
    public NCBiotechActorState Measure(EntityUid actor)
    {
        var grip = 1f;
        var vision = 1f;
        if (TryComp<NCBodyFunctionComponent>(actor, out var function))
        {
            // The better hand does the delicate work, the other only steadies it.
            grip = MathF.Max(function.LeftGrip, function.RightGrip) * 0.75f + MathF.Min(function.LeftGrip, function.RightGrip) * 0.25f;
            vision = function.Vision;
        }

        var modifiers = _pharmacology.GetModifiers(actor);
        var steadiness = Math.Clamp(modifiers.MotorControlMultiplier, 0f, 1f);
        var attention = Math.Clamp(1f - modifiers.Sedation * 0.6f - modifiers.Anesthesia, 0f, 1f);
        return new NCBiotechActorState(grip, vision, steadiness, attention);
    }
}
