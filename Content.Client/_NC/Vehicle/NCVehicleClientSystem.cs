// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.Physics;
using Robust.Client.Graphics;
using Content.Client.Atmos.EntitySystems;
using System.Numerics;
using Robust.Client.Player;
using Robust.Shared.Input.Binding;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;

namespace Content.Client._NC.Vehicle;

/// <summary>Input context and prediction ownership; body forces remain in the shared controller.</summary>
public sealed partial class NCVehicleClientSystem : EntitySystem
{
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private SpriteSystem _sprites = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlays.AddOverlay(new NCVehicleWindshieldOverlay(EntityManager, _players));
        SubscribeLocalEvent<NCVehicleBodyComponent, AppearanceChangeEvent>(OnAppearance);
        SubscribeLocalEvent<NCVehicleDriveComponent, UpdateIsPredictedEvent>(OnPredicted);
        SubscribeLocalEvent<NCVehicleDriveComponent, AfterAutoHandleStateEvent>(OnState);
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);
        var context = _input.Contexts.New("nc-vehicle", "common");
        var functions = new[] { NCVehicleKeyFunctions.Throttle, NCVehicleKeyFunctions.Brake,
            NCVehicleKeyFunctions.Clutch, NCVehicleKeyFunctions.Handbrake, NCVehicleKeyFunctions.GearUp,
            NCVehicleKeyFunctions.GearDown, NCVehicleKeyFunctions.GearLeft, NCVehicleKeyFunctions.GearRight,
            NCVehicleKeyFunctions.SteerLeft, NCVehicleKeyFunctions.SteerRight };
        var builder = CommandBinds.Builder;
        foreach (var function in functions)
        {
            context.AddFunction(function);
            // BUI aggregates these held keys into timestamped prediction messages.
            builder.Bind(function, InputCmdHandler.FromDelegate());
        }
        builder.Register<NCVehicleClientSystem>();
    }

    public override void Shutdown()
    {
        CommandBinds.Unregister<NCVehicleClientSystem>();
        _overlays.RemoveOverlay<NCVehicleWindshieldOverlay>();
        _input.Contexts.Remove("nc-vehicle");
        base.Shutdown();
    }

    private void OnPredicted(Entity<NCVehicleDriveComponent> ent, ref UpdateIsPredictedEvent args)
    {
        if (ent.Comp.Driver != null && ent.Comp.Driver == _players.LocalEntity)
            args.IsPredicted = true;
    }

    private void OnState(Entity<NCVehicleDriveComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _physics.UpdateIsPredicted(ent);
    }

    private void OnAppearance(Entity<NCVehicleBodyComponent> ent, ref AppearanceChangeEvent args)
    {
        // The high-resolution Supra body uses a small entity scale; compensate only the stock fire effect layer.
        if (args.Sprite is { } sprite && sprite.Scale.X > 0 && sprite.Scale.Y > 0 &&
            _sprites.LayerMapTryGet((ent.Owner, sprite), FireVisualLayers.Fire, out var layer, false))
            _sprites.LayerSetScale((ent.Owner, sprite), layer, new Vector2(1 / sprite.Scale.X, 1 / sprite.Scale.Y));
    }

    private void OnPlayerAttached(LocalPlayerAttachedEvent args) => RefreshPrediction();
    private void OnPlayerDetached(LocalPlayerDetachedEvent args) => RefreshPrediction();

    private void RefreshPrediction()
    {
        // Player attachment is rare; refresh existing vehicles even if no component state changed.
        var query = EntityQueryEnumerator<NCVehicleDriveComponent>();
        while (query.MoveNext(out var uid, out _))
            _physics.UpdateIsPredicted(uid);
    }
}
