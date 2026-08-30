// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.GameRules.Components;
using Content.Server.GameTicking.Rules;
using Content.Shared.GameTicking.Components;

namespace Content.Server._NC.GameRules;

/// <summary>
/// Owns the canonical game-rule identity of a Night City round. Living City domains intentionally start from the
/// game ticker's <c>InRound</c> transition, keeping one authoritative active-world epoch for every city subsystem.
/// </summary>
public sealed partial class NCNightCityRuleSystem : GameRuleSystem<NCNightCityRuleComponent>
{
    private readonly HashSet<EntityUid> _activeRules = [];

    /// <summary>
    /// True while at least one authoritative Night City rule is active.
    /// </summary>
    public bool IsActive => _activeRules.Count != 0;

    public override void Shutdown()
    {
        _activeRules.Clear();
        base.Shutdown();
    }

    protected override void Started(
        EntityUid uid,
        NCNightCityRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        _activeRules.Add(uid);
        Log.Info($"Night City game rule started on {ToPrettyString(uid)}.");
    }

    protected override void Ended(
        EntityUid uid,
        NCNightCityRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);
        _activeRules.Remove(uid);
        Log.Info($"Night City game rule ended on {ToPrettyString(uid)}.");
    }
}
