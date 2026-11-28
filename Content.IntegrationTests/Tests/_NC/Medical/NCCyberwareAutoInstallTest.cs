// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Cyberware.AutoInstall;
using Content.Server._NC.Medical.Cyberware.Presets;
using Content.Shared._NC.Medical.Cyberware.AutoImplanter;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
public sealed class NCCyberwareAutoInstallTest : GameTest
{
    [SidedDependency(Side.Server)] private NCCyberwareAutoInstallSystem _auto = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private NCCyberwarePresetSystem _presets = default!;
    [SidedDependency(Side.Server)] private NCAutoImplanterSystem _implanter = default!;

    [Test, RunOnSide(Side.Server)]
    public void EveryCatalogImplantInstallsReadyToWork()
    {
        var problems = new List<string>();
        var items = _auto.GetInstallableItems().ToList();
        Assert.That(items, Is.Not.Empty);
        foreach (var (item, profile) in items)
        {
            var body = Human();
            if (!_auto.TryAutoInstall(body, item, out var failure))
            {
                problems.Add($"{item}: {failure}");
                continue;
            }

            var module = Installed(body, profile.ID);
            if (module == null)
            {
                problems.Add($"{item}: not found in the body");
                continue;
            }

            var state = SComp<NCCyberwareComponent>(module.Value);
            if (!state.Calibrated || !state.Operational)
            {
                problems.Add($"{item}: calibrated={state.Calibrated} operational={state.Operational} efficiency={state.FunctionalEfficiency} power={state.PowerGranted} dep={state.NeuralDependency} support={state.SupportReady} neural={state.NeuralState} asm={(SEntMan.TryGetComponent<NCCyberwareAssemblyComponent>(module.Value, out var asm) ? asm.FunctionalEfficiency : -1)}");
            }
        }

        Assert.That(problems, Is.Empty, string.Join("\n", problems));
    }

    [Test, RunOnSide(Side.Server)]
    public void LinearFrameBringsItsGraftNeuralLinkAndPower()
    {
        var body = Human();
        Assert.That(_auto.TryAutoInstall(body, "NCImplantLinearFrameSigma", out var failure), Is.True, failure);
        Assert.That(_auto.HasInstalled(body, "NCGraftedMuscleBoneLace"), Is.True);
        Assert.That(SComp<NCCyberwareComponent>(Installed(body, "NCImplantedLinearFrameSigma")!.Value).Operational, Is.True);
        Assert.That(_cyberware.GetInstalledModuleEntities(body, 64).Any(SEntMan.HasComponent<NCCyberwarePowerBusComponent>), Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void TraumaTeamPresetFitsOnceWithoutDuplicates()
    {
        var body = Human();
        Assert.That(_presets.TryApplyPreset(body, "NCPresetTraumaTeamFrame"), Is.True);
        var count = _cyberware.GetInstalledModuleEntities(body, 64).Count;
        Assert.That(_presets.TryApplyPreset(body, "NCPresetTraumaTeamFrame"), Is.True);
        Assert.That(_cyberware.GetInstalledModuleEntities(body, 64).Count, Is.EqualTo(count));
        Assert.That(SComp<NCCyberwareComponent>(Installed(body, "NCImplantedLinearFrameSigma")!.Value).Operational, Is.True);
    }

    [Test, RunOnSide(Side.Server)]
    public void AutoImplanterOnlyOffersItsCatalog()
    {
        var body = Human();
        var implanter = SEntMan.SpawnEntity("NCAutoImplanter", SComp<TransformComponent>(body).Coordinates);
        var entity = (implanter, SComp<NCAutoImplanterComponent>(implanter));
        Assert.That(_implanter.TryInstall(entity, body, "NCBorgFrameTorso"), Is.False);
        Assert.That(_implanter.TryInstall(entity, body, "NCSandevistanImplant"), Is.True);
        Assert.That(_auto.HasInstalled(body, "NCSandevistan"), Is.True);
    }

    private EntityUid Human()
    {
        _maps.CreateMap(out var map);
        return SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
    }

    private EntityUid? Installed(EntityUid body, string profile)
    {
        foreach (var uid in _cyberware.GetInstalledModuleEntities(body, 64)
                     .Concat(SComp<BodyComponent>(body).Organs!.ContainedEntities))
        {
            if (SEntMan.TryGetComponent<NCCyberwareComponent>(uid, out var module) && module.Profile == profile)
                return uid;
        }

        return null;
    }
}
