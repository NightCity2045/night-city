// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.Vehicle;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NC.Vehicle;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Robust.Client.UserInterface;
using Robust.Client.Input;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Client.UserInterface.Controls;

namespace Content.IntegrationTests.Tests._NC.Vehicle;

[TestFixture]
public sealed class NCVehicleWorkshopInteractionTest : InteractionTest
{
    [Test]
    public async Task WorkshopShowsCalibrationsAndRepairsOnlyTheChosenBodyZone()
    {
        try
        {
            await CheckWorkshop();
        }
        catch (Exception error)
        {
            // Keep the original assertion visible if pool disposal emits a secondary NUnit warning.
            TestContext.Error.WriteLine(error);
            throw;
        }
    }

    private async Task CheckWorkshop()
    {
        var car = await SpawnEntity("NCToyotaSupra1996", SEntMan.GetCoordinates(TargetCoords));
        Target = SEntMan.GetNetEntity(car);
        var benchCoordinates = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(0, 1));
        await SetTile(Plating, SEntMan.GetNetCoordinates(benchCoordinates), MapData.Grid);
        await SpawnEntity("NCVehicleRepairBench", benchCoordinates);
        var gauge = await SpawnEntity("NCVehicleAlignmentGauge", SEntMan.GetCoordinates(PlayerCoords));
        await Server.WaitAssertion(() =>
        {
            var hands = Server.System<SharedHandsSystem>();
            Assert.That(hands.TryPickup(SPlayer, gauge), Is.True);
            var service = Server.System<NCVehicleServiceSystem>();
            Assert.That(service.TryStart(car, SPlayer, "", NCVehicleServiceAction.Hood), Is.True);
            Assert.That(service.CanCalibrate(car, SPlayer, "NCVehicleStreetCalibration"), Is.True);
            Assert.That(service.CanCalibrate(car, SPlayer, "UnknownCalibration"), Is.False);
            Assert.That(service.TryOpen(car, SPlayer), Is.True);
        });
        await RunTicks(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.TryGetComponent<Content.Server._NC.Vehicle.Workshop.NCVehicleWorkshopSessionComponent>(SPlayer, out var session), Is.True, "Workshop observer created");
            Assert.That(session.Vehicle, Is.EqualTo(car));
        });
        var window = GetWindow<NCVehicleServiceWindow>();
        await Client.WaitAssertion(() =>
            Assert.That(GetControlFromField<OptionButton>("Calibration", window).ItemCount, Is.EqualTo(3)));
        await Server.WaitAssertion(() =>
            Assert.That(Server.System<NCVehicleServiceSystem>().TryStartCalibration(car, SPlayer, "NCVehicleStreetCalibration"), Is.True));
        // Advance the real shared DoAfter clock with the same tool and stationary mechanic.
        await RunSeconds(31);
        await Server.WaitAssertion(() =>
            Assert.That(SEntMan.GetComponent<NCVehicleDriveComponent>(car).Calibration?.Id, Is.EqualTo("NCVehicleStreetCalibration")));
        var supplies = await SpawnEntity("NCVehicleBodyworkRepairKit", SEntMan.GetCoordinates(PlayerCoords));
        await Server.WaitAssertion(() =>
        {
            var hands = Server.System<SharedHandsSystem>();
            Assert.That(hands.TryDrop(SPlayer, gauge), Is.True);
            Assert.That(hands.TryPickup(SPlayer, supplies), Is.True);
            var damage = Server.System<NCVehicleDamageSystem>();
            Assert.That(damage.TryImpact(car, Vector2.UnitY, Vector2.UnitY, 18000, 0, "front-center"), Is.True);
            var shell = SEntMan.GetComponent<NCVehicleBodyComponent>(car);
            var before = shell.Deformation["front-center"];
            Assert.That(damage.TryRepairBody(car, SPlayer, supplies, "front-center"), Is.True);
            Assert.That(shell.Deformation["front-center"], Is.LessThan(before));
            Assert.That(shell.Deformation.ContainsKey("rear-center"), Is.False);
            Assert.That(SEntMan.EntityExists(supplies), Is.False);
        });
    }

}
