// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.UserInterface;

namespace Content.Shared._NC.Meta.Devices;

/// <summary>Cancels a person's attempt to open a gated device's interface until its program decides.</summary>
public abstract class SharedNCMetaGateSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaGateComponent, ActivatableUIOpenAttemptEvent>(OnOpenAttempt);
    }

    private void OnOpenAttempt(EntityUid uid, NCMetaGateComponent component, ActivatableUIOpenAttemptEvent args)
    {
        if (!TryHandleOpenAttempt((uid, component), args.User))
            args.Cancel();
    }

    /// <summary>Returns true when the person may open the interface right now. May start an asynchronous request.</summary>
    protected abstract bool TryHandleOpenAttempt(Entity<NCMetaGateComponent> device, EntityUid user);
}
