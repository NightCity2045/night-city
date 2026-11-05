// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.GameStates;
using Content.Shared.Interaction;

namespace Content.Shared._NC.Medical.Cyberware.Arms;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCArmPocketComponent : Component
{
    [DataField, AutoNetworkedField] public EntityUid? Body;
}

/// <summary>The implanted pocket is two containers deep. Only its current wearer can access its UI.</summary>
public sealed partial class NCArmPocketSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCArmPocketComponent, AccessibleOverrideEvent>(OnAccess);
        SubscribeLocalEvent<NCArmPocketComponent, BoundUserInterfaceMessageAttempt>(OnMessage);
    }

    private void OnAccess(Entity<NCArmPocketComponent> ent, ref AccessibleOverrideEvent args)
    {
        if (args.Target != ent.Owner || ent.Comp.Body == null) return;
        args.Handled = true;
        args.Accessible = args.User == ent.Comp.Body;
    }

    private void OnMessage(Entity<NCArmPocketComponent> ent, ref BoundUserInterfaceMessageAttempt args)
    {
        if (ent.Comp.Body != args.Actor) args.Cancel();
    }
}
