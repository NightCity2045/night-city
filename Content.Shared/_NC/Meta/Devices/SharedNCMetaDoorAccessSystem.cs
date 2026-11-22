// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.Doors;

namespace Content.Shared._NC.Meta.Devices;

/// <summary>
/// Single interception point for every physical opening path (click, bump, hand): they all raise
/// <see cref="BeforeDoorOpenedEvent"/> from the vanilla door system before the access check.
/// </summary>
public abstract class SharedNCMetaDoorAccessSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaDoorAccessComponent, BeforeDoorOpenedEvent>(OnBeforeDoorOpened);
    }

    private void OnBeforeDoorOpened(Entity<NCMetaDoorAccessComponent> ent, ref BeforeDoorOpenedEvent args)
    {
        // A missing user is a trusted internal path; only people are subject to program decisions.
        if (args.User is { } user && !TryHandleOpenAttempt(ent, user))
            args.Cancel();
    }

    /// <summary>Returns true when the person may open the door right now. May start an asynchronous request.</summary>
    protected abstract bool TryHandleOpenAttempt(Entity<NCMetaDoorAccessComponent> door, EntityUid user);
}
