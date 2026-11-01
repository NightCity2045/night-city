// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Reflection;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>Test-only observation of the actual injectable solution, before metabolism can consume a dose.</summary>
[RegisterComponent]
public sealed partial class NCMetaDoseObservationComponent : Component
{
    public EntityUid Solution;
    public FixedPoint2 Previous;
    public FixedPoint2 Received;
    public int Increases;
}

[Reflect(false)]
public sealed class NCMetaDoseObservationSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaDoseObservationComponent, SolutionChangedEvent>(OnChanged);
    }

    private void OnChanged(Entity<NCMetaDoseObservationComponent> ent, ref SolutionChangedEvent args)
    {
        if (args.Solution.Owner != ent.Comp.Solution) return;
        var water = args.Solution.Comp.Solution.GetTotalPrototypeQuantity("Water");
        if (water > ent.Comp.Previous)
        {
            ent.Comp.Received += water - ent.Comp.Previous;
            ent.Comp.Increases++;
        }
        ent.Comp.Previous = water;
    }
}
