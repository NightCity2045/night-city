// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// The single door between laboratory work and the action-resolution layer. Every professional procedure asks
/// here for its quality; the answer is bounded so no resolver can zero a batch or make it perfect.
/// </summary>
public sealed class NCBiotechActionSystem : EntitySystem
{
    public const float MinimumQuality = 0.5f;
    public const float MaximumQuality = 1.25f;

    public NCBiotechActionResult Resolve(
        EntityUid actor,
        ProtoId<NCBiotechProcedurePrototype> procedure,
        EntityUid? equipment = null,
        IReadOnlyList<Guid>? inputBatches = null)
    {
        var request = new NCBiotechActionRequest(
            NCActionResolutionId.New(), actor, procedure, equipment, inputBatches ?? []);
        var resolve = new NCBiotechActionResolveEvent(request);
        RaiseLocalEvent(ref resolve);
        var quality = float.IsFinite(resolve.Quality)
            ? Math.Clamp(resolve.Quality, MinimumQuality, MaximumQuality)
            : 1f;
        return new NCBiotechActionResult(request.ResolutionId, quality);
    }
}
