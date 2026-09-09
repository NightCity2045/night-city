// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle.Workshop;

/// <summary>A removed assembly carries its actual mass into the world; carrier children are counted once.</summary>
public sealed partial class NCVehiclePartPhysicsSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehiclePartItemComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<NCVehiclePartItemComponent> ent, ref MapInitEvent args) => TryRefresh(ent);

    public bool TryRefresh(EntityUid uid)
    {
        if (!TryComp<NCVehiclePartItemComponent>(uid, out var item) || !_prototypes.TryIndex(item.Part, out var part) ||
            !TryComp<PhysicsComponent>(uid, out var body) || body.Mass <= 0 || !TryComp<FixturesComponent>(uid, out var fixtures))
            return false;
        var mass = part.Mass;
        if (TryComp<NCVehicleDetachedAssemblyComponent>(uid, out var bundle))
            foreach (var (slot, child) in bundle.Parts)
                if (slot != bundle.RootSlot && _prototypes.TryIndex(child.Prototype, out var childPart)) mass += childPart.Mass;
        var ratio = mass / body.Mass;
        foreach (var (id, fixture) in fixtures.Fixtures)
            _physics.SetDensity(uid, id, fixture, fixture.Density * ratio, manager: fixtures);
        _physics.ResetMassData(uid, fixtures, body);
        return true;
    }
}
