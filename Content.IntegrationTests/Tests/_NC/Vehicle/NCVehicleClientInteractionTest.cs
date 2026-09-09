// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Collections.Generic;
using System.Linq;
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
using Robust.Shared.Localization;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NC.Vehicle;

[TestFixture]
public sealed class NCVehicleClientInteractionTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";

    [TestCase(false)]
    [TestCase(true)]
    public async Task IgnitionButtonStartsEngineThroughTheClientPanel(bool registered)
    {
        try
        {
            var car = await SpawnEntity("NCToyotaSupra1996", SEntMan.GetCoordinates(TargetCoords));
            Target = SEntMan.GetNetEntity(car);
            EntityUid key = default;
            await Server.WaitAssertion(() =>
            {
                // Give all four tyres a real road for the subsequent launch, including the run-out area.
                var tiles = new List<(Vector2i, Tile)>();
                for (var x = -5; x <= 5; x++)
                for (var y = -5; y <= 20; y++)
                    tiles.Add((new Vector2i(x, y), new Tile(TileMan[Plating].TileId)));
                MapSystem.SetTiles(MapData.Grid, tiles);
                if (!registered)
                    return;
                Assert.That(Server.System<NCVehicleOwnershipSystem>().TryRegister(car, SPlayer), Is.True);
                var hands = Server.System<SharedHandsSystem>();
                key = hands.EnumerateHeld(SPlayer).Single(uid => SEntMan.HasComponent<NCVehicleKeyComponent>(uid));
                Assert.That(hands.TryDrop(SPlayer, key), Is.True);
            });
            await Server.WaitAssertion(() => Assert.That(Server.System<NCVehicleOccupantSystem>().TryEnter(car, SPlayer), Is.True));
            await RunTicks(5);
            if (registered)
            {
                await ClickControl<NCVehicleDriverWindow>("Ignition");
                await RunSeconds(1);
                await Server.WaitAssertion(() =>
                {
                    var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
                    Assert.That(drive.EngineRunning || drive.Cranking, Is.False);
                    Assert.That(Server.System<NCVehicleControlSystem>().CanStartEngine(car, SPlayer, out var reason), Is.False);
                    Assert.That(reason, Is.EqualTo((LocId) "nc-vehicle-start-key"));
                });
                await Client.WaitAssertion(() => Assert.That(
                    GetControlFromField<RichTextLabel>("EngineStatus", GetWindow<NCVehicleDriverWindow>()).Text,
                    Is.EqualTo(Client.ResolveDependency<ILocalizationManager>().GetString("nc-vehicle-start-key"))));
                await Server.WaitAssertion(() => Assert.That(Server.System<SharedHandsSystem>().TryPickupAnyHand(SPlayer, key), Is.True));
                await RunTicks(5);
            }
            // Exercise the actual button and predicted BUI delivery, rather than invoking the control system.
            await ClickControl<NCVehicleDriverWindow>("Ignition");
            await RunSeconds(2);
            await Server.WaitAssertion(() =>
            {
                var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
                Assert.That(drive.EngineRunning, Is.True,
                    $"RPM={drive.EngineRpm}, cranking={drive.Cranking}, assembly={drive.AssemblyReady}, gear={drive.Gear}");
                Assert.That(drive.EngineRpm, Is.GreaterThan(300));
            });
            await Client.WaitAssertion(() => Assert.That(
                CEntMan.GetComponent<NCVehicleDriveComponent>(ToClient(Target.Value)).EngineRunning, Is.True));

            await Client.WaitPost(() =>
            {
                InputManager.KeyDown(new KeyEventArgs(Keyboard.Key.Shift, false, false, false, true, false, 0));
                InputManager.KeyDown(new KeyEventArgs(Keyboard.Key.A, false, false, false, true, false, 0));
                InputManager.KeyDown(new KeyEventArgs(Keyboard.Key.W, false, false, false, true, false, 0));
                InputManager.KeyDown(new KeyEventArgs(Keyboard.Key.Q, false, false, false, true, false, 0));
            });
            try
            {
                await RunSeconds(1);
                await Client.WaitPost(() => InputManager.KeyUp(new KeyEventArgs(Keyboard.Key.Shift, false, false, false, false, false, 0)));
                await RunSeconds(2);
                await Server.WaitAssertion(() =>
                {
                    var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
                    Assert.That(drive.Gear, Is.EqualTo(1));
                    Assert.That(drive.Clutch, Is.Zero);
                    Assert.That(drive.EngineRunning, Is.True);
                    Assert.That(SEntMan.GetComponent<PhysicsComponent>(car).LinearVelocity.Y, Is.GreaterThan(1));
                });
            }
            finally
            {
                await Client.WaitPost(() =>
                {
                    foreach (var keyCode in new[] { Keyboard.Key.Shift, Keyboard.Key.A, Keyboard.Key.W, Keyboard.Key.Q })
                        InputManager.KeyUp(new KeyEventArgs(keyCode, false, false, false, false, false, 0));
                });
            }
        }
        catch (Exception error)
        {
            TestContext.Error.WriteLine(error);
            throw;
        }
    }

    [Test]
    public async Task SimultaneousClutchAndGateKeysEngageFirstThroughTheClientPanel()
    {
        var car = await SpawnEntity("NCToyotaSupra1996", SEntMan.GetCoordinates(TargetCoords));
        Target = SEntMan.GetNetEntity(car);
        await Server.WaitAssertion(() => Assert.That(Server.System<NCVehicleOccupantSystem>().TryEnter(car, SPlayer), Is.True));
        await RunTicks(5);
        await Client.WaitAssertion(() =>
        {
            InputManager.KeyDown(new KeyEventArgs(Keyboard.Key.Shift, false, false, false, true, false, 0));
            InputManager.KeyDown(new KeyEventArgs(Keyboard.Key.A, false, false, false, true, false, 0));
            InputManager.KeyDown(new KeyEventArgs(Keyboard.Key.W, false, false, false, true, false, 0));
        });
        try
        {
            // Hold actual key bindings across frames: this exercises aggregation, BUI delivery and authority.
            await RunTicks(30);
            await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<NCVehicleDriveComponent>(car).Gear, Is.EqualTo(1)));
            await Client.WaitAssertion(() => Assert.That(CEntMan.GetComponent<NCVehicleDriveComponent>(ToClient(Target.Value)).Gear, Is.EqualTo(1)));
        }
        finally
        {
            await Client.WaitPost(() =>
            {
                InputManager.KeyUp(new KeyEventArgs(Keyboard.Key.W, false, false, false, true, false, 0));
                InputManager.KeyUp(new KeyEventArgs(Keyboard.Key.A, false, false, false, true, false, 0));
                InputManager.KeyUp(new KeyEventArgs(Keyboard.Key.Shift, false, false, false, false, false, 0));
            });
        }
    }

    [Test]
    public async Task DriverGetsPredictedChassisAndContentOnlySteeringControl()
    {
        var car = await SpawnEntity("NCToyotaSupra1996", SEntMan.GetCoordinates(TargetCoords));
        Target = SEntMan.GetNetEntity(car);
        await Server.WaitAssertion(() => Assert.That(Server.System<NCVehicleOccupantSystem>().TryEnter(car, SPlayer), Is.True));
        await RunTicks(5);
        var clientCar = ToClient(Target.Value);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.GetComponent<NCVehicleDriveComponent>(clientCar).Driver, Is.EqualTo(CPlayer));
            Assert.That(CEntMan.GetComponent<PhysicsComponent>(clientCar).Predict, Is.True);
            Assert.That(CUiSys.IsUiOpen(clientCar, NCVehicleUiKey.Driver), Is.True);
        });
        var window = GetWindow<NCVehicleDriverWindow>();
        var wheel = GetControlFromField<NCVehicleSteeringWheel>("Steering", window);
        var point = wheel.Size / 2 + new Vector2(40, 0);
        var screen = new ScreenCoordinates(wheel.GlobalPixelPosition + point * wheel.UIScale, wheel.Window!.Id);
        await Client.DoGuiEvent(wheel, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, BoundKeyState.Down,
            screen, true, point, point * wheel.UIScale));
        await Client.WaitAssertion(() => Assert.That(wheel.Held, Is.True));
        await Client.DoGuiEvent(wheel, new GUIBoundKeyEventArgs(EngineKeyFunctions.EscapeMenu, BoundKeyState.Down,
            screen, false, point, point * wheel.UIScale));
        await Client.WaitAssertion(() => Assert.That(wheel.Held, Is.False));
        await Server.WaitAssertion(() =>
        {
            var occupants = Server.System<NCVehicleOccupantSystem>();
            Assert.That(occupants.TryToggleBelt(car, SPlayer), Is.True);
            Assert.That(occupants.TryExit(car, SPlayer), Is.False);
            Assert.That(occupants.TryToggleBelt(car, SPlayer), Is.True);
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
            drive.PendingShift = NCVehicleDriverAction.GearUp;
            drive.ShiftStatus = NCVehicleShiftStatus.WaitingForClutch;
            Assert.That(occupants.TryExit(car, SPlayer), Is.True);
            Assert.That(drive.PendingShift, Is.Null, "A subsequent driver must not inherit a queued shift.");
        });
        await RunTicks(5);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.GetComponent<NCVehicleDriveComponent>(clientCar).Driver, Is.Null);
            Assert.That(CUiSys.IsUiOpen(clientCar, NCVehicleUiKey.Driver), Is.False);
        });
    }
}
