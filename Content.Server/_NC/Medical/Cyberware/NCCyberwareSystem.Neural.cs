// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    private bool _restoringLegacyNeuralHosts;
    /// <summary>Finds a separate live foundation in the same body. A neural edge is never a container edge.</summary>
    public bool TryGetNeuralFoundation(EntityUid uid, out EntityUid foundation) =>
        TryGetNeuralFoundation(uid, null, out foundation);

    private bool TryGetNeuralFoundation(EntityUid uid, IReadOnlyList<EntityUid>? modules, out EntityUid foundation)
    {
        foundation = default;
        if (!TryComp<NCCyberwareComponent>(uid, out var module) ||
            !TryGetProfile(module.Profile, out var profile) || profile.RequiredNeuralInterface is not { } required ||
            !TryResolveOwningBody(uid, out var body))
            return false;
        modules ??= CollectCyberware(body);
        foreach (var candidate in modules)
        {
            if (candidate == uid || TerminatingOrDeleted(candidate) ||
                !TryComp<NCCyberwareComponent>(candidate, out var provider) || provider.Profile != required)
                continue;
            foundation = candidate;
            return true;
        }
        return false;
    }

    private (NCCyberwareNeuralDependencyState State, float Efficiency) ResolveNeuralDependency(
        EntityUid uid, NCCyberwarePrototype profile, TimeSpan now, IReadOnlyList<EntityUid>? modules)
    {
        if (profile.RequiredNeuralInterface == null)
            return NCCyberwareNeuralDependency.Evaluate(false, false, false, 0f);
        if (!TryGetNeuralFoundation(uid, modules, out var foundation))
            return NCCyberwareNeuralDependency.Evaluate(true, false, false, 0f);
        var provider = Comp<NCCyberwareComponent>(foundation);
        var definition = _prototypes.Index(provider.Profile);
        // Only one foundation layer is supported. Validation rejects dependent providers and cycles.
        if (definition.RequiredNeuralInterface != null)
            return NCCyberwareNeuralDependency.Evaluate(true, true, false, 0f);
        UpdateNeuralState(provider, definition, now);
        UpdateOperational(foundation, provider, definition, now);
        return NCCyberwareNeuralDependency.Evaluate(true, true, provider.Calibrated, provider.FunctionalEfficiency);
    }

    /// <summary>Searches only the selected live region's bounded anatomical subtree for an implant bay.</summary>
    public bool TryFindInstallHost(EntityUid patient, EntityUid region, EntityUid module,
        out EntityUid host, out ProtoId<NCCyberwareSlotPrototype> slot)
    {
        host = default;
        slot = default;
        var pending = new Queue<EntityUid>();
        var seen = new HashSet<EntityUid>();
        pending.Enqueue(region);
        while (pending.TryDequeue(out var candidate) && seen.Count < MaximumCyberwarePerBody)
        {
            if (!seen.Add(candidate) || !TryComp<OrganComponent>(candidate, out var organ) || organ.Body != patient)
                continue;
            if (TryFindInstallSlot(patient, candidate, module, out slot))
            {
                host = candidate;
                return true;
            }
            if (!TryComp<ParentOrganComponent>(candidate, out var parent))
                continue;
            foreach (var child in parent.Children)
                pending.Enqueue(child);
        }
        return false;
    }
}
