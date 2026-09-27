// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Network;

namespace Content.Shared.Inventory;

public abstract partial class InventorySystem
{
    [Dependency] private INetManager _ncNetManager = default!;

    /// <summary>
    /// Displays a predictable inventory rejection only on the initiating client.
    /// The server still performs the authoritative validation, but does not send a second popup localized in the
    /// server's language, which cannot be deduplicated against the client's localized prediction.
    /// </summary>
    private void NCShowPredictedEquipFailure(string reason, EntityUid actor)
    {
        if (!_ncNetManager.IsClient)
            return;

        _popup.PopupCursor(Loc.GetString(reason), actor);
    }
}
