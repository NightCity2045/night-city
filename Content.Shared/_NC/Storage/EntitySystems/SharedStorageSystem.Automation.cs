// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.Hands.Components;
using Content.Shared.Storage.Events;

namespace Content.Shared.Storage.EntitySystems;

public abstract partial class SharedStorageSystem
{
    /// <summary>Uses native storage locks, attempt events, capacity, whitelist and real hand restrictions.</summary>
    public bool TryStoreHeld(Entity<StorageComponent?> storage, Entity<HandsComponent?> user, EntityUid expectedItem, Func<bool> guard)
    {
        if (!Resolve(storage, ref storage.Comp, false) || !Resolve(user, ref user.Comp, false) || !guard() ||
            !_interactionSystem.InRangeUnobstructed(user.Owner, storage.Owner) ||
            !CanInteract(user.Owner, (storage.Owner, storage.Comp), ActionBlocker.CanInteract(user.Owner, storage.Owner)) ||
            _sharedHandsSystem.GetActiveItem(user.Owner) != expectedItem) return false;
        var attempt = new StorageInteractUsingAttemptEvent();
        RaiseLocalEvent(storage.Owner, ref attempt);
        if (attempt.Cancelled || !CanInsert(storage.Owner, expectedItem, out _, storage.Comp) ||
            !_sharedHandsSystem.CanDrop(user.Owner, expectedItem) || !guard() ||
            !_interactionSystem.InRangeUnobstructed(user.Owner, storage.Owner)) return false;
        // Do not merge stacks: the entire physical cargo object must move, or remain in its hand.
        return Insert(storage.Owner, expectedItem, out _, user: user.Owner, storageComp: storage.Comp, stackAutomatically: false) &&
            storage.Comp.Container.Contains(expectedItem) && !_sharedHandsSystem.IsHolding(user.Owner, expectedItem);
    }
}
