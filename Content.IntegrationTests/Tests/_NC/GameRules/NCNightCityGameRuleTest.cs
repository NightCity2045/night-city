// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.GameRules;
using Content.Server._NC.GameRules.Components;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Shared.CCVar;
using Content.Shared.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.GameRules;

[TestFixture]
[TestOf(typeof(NCNightCityRuleSystem))]
public sealed class NCNightCityGameRuleTest : GameTest
{
    private const string NightCityPreset = "NightCity";
    private const string NightCityRule = "NCNightCityRule";

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
        DummyTicker = false,
    };

    [Test]
    public void PresetAndRuleLifecycleAreCanonical()
    {
        var ticker = Server.System<GameTicker>();
        var system = Server.System<NCNightCityRuleSystem>();
        var prototypes = Server.ResolveDependency<IPrototypeManager>();
        var componentFactory = Server.ResolveDependency<IComponentFactory>();
        var preset = prototypes.Index<GamePresetPrototype>(NightCityPreset);

        Assert.Multiple(() =>
        {
            Assert.That(Server.CfgMan.GetCVar(CCVars.GameLobbyDefaultPreset), Is.EqualTo("NightCity"));
            Assert.That(preset.Rules.Select(static rule => rule.Id), Is.EqualTo(new[] { NightCityRule }));
            Assert.That(prototypes.Index<EntityPrototype>(NightCityRule)
                .HasComponent<NCNightCityRuleComponent>(componentFactory), Is.True);
            Assert.That(system.IsActive, Is.False);
        });

        Assert.That(ticker.StartGameRule(NightCityRule, out var ruleEntity), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(ticker.IsGameRuleActive(ruleEntity), Is.True);
            Assert.That(system.IsActive, Is.True);
        });

        Assert.That(ticker.EndGameRule(ruleEntity), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(ticker.IsGameRuleActive(ruleEntity), Is.False);
            Assert.That(system.IsActive, Is.False);
        });
    }
}
