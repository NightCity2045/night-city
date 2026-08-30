// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public readonly record struct NCCityPhysicalStateTreeCounters(
    long Evaluations,
    long Transitions,
    long InvalidDefinitions);

/// <summary>
/// Resolves a data-driven physical HFSM rule from already-authoritative ECS conditions. The resolver has no Update
/// loop and performs no world mutation, so the physical executor remains the only owner of route state transitions.
/// </summary>
public sealed partial class NCCityPhysicalStateTreeSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private long _evaluations;
    private long _transitions;
    private long _invalidDefinitions;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public bool TryResolve(
        ProtoId<NCCityPhysicalStateTreePrototype> profileId,
        IReadOnlySet<NCCityPhysicalCondition> conditions,
        out NCCityPhysicalStateRule rule,
        out NCCityPhysicalFallback blockedFallback)
    {
        rule = default!;
        blockedFallback = NCCityPhysicalFallback.SafeIdle;
        _evaluations++;
        if (!_prototypes.TryIndex(profileId, out var profile) || profile == null || !Validate(profile))
        {
            _invalidDefinitions++;
            return false;
        }

        NCCityPhysicalStateRule? selected = null;
        foreach (var candidate in profile.Rules)
        {
            if (!Matches(candidate, conditions) || selected != null && selected.Priority >= candidate.Priority)
                continue;
            selected = candidate;
        }

        if (selected == null)
            return false;
        rule = selected;
        blockedFallback = profile.BlockedFallback;
        return true;
    }

    public void RecordTransition() => _transitions++;

    public NCCityPhysicalStateTreeCounters GetCounters() => new(
        _evaluations,
        _transitions,
        _invalidDefinitions);

    internal static bool Matches(
        NCCityPhysicalStateRule rule,
        IReadOnlySet<NCCityPhysicalCondition> conditions)
    {
        foreach (var required in rule.RequiredAll)
        {
            if (!conditions.Contains(required))
                return false;
        }
        foreach (var forbidden in rule.Forbidden)
        {
            if (conditions.Contains(forbidden))
                return false;
        }
        return true;
    }

    internal static bool Validate(NCCityPhysicalStateTreePrototype profile)
    {
        if (profile.MaximumRules <= 0 || profile.Rules.Count == 0 || profile.Rules.Count > profile.MaximumRules)
            return false;

        var priorities = new HashSet<int>();
        var hasFallback = false;
        foreach (var rule in profile.Rules)
        {
            if (!priorities.Add(rule.Priority) || rule.EvaluationInterval <= TimeSpan.Zero ||
                rule.RequiredAll.Overlaps(rule.Forbidden))
            {
                return false;
            }
            hasFallback |= rule.RequiredAll.Count == 0 && rule.Forbidden.Count == 0;
        }
        return hasFallback;
    }

    private void Reset()
    {
        _evaluations = 0;
        _transitions = 0;
        _invalidDefinitions = 0;
    }
}
