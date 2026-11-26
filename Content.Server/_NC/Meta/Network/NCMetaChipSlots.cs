// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.UI;
using Content.Shared.Containers.ItemSlots;

namespace Content.Server._NC.Meta.Network;

/// <summary>The program chips sitting in a device's slots. A server may have several slots, so nothing here assumes one.</summary>
public static class NCMetaChipSlots
{
    /// <summary>The chips in slot order, or nothing when the device has no slots.</summary>
    public static IEnumerable<EntityUid> Chips(ItemSlotsComponent? slots) => slots == null ? [] : slots.Slots
        .Where(pair => NCMetaChipSlot.IsSlot(pair.Key) && pair.Value.Item != null)
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Value.Item!.Value);
}
