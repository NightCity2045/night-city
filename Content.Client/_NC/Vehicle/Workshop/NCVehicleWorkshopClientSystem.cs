// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Workshop;

namespace Content.Client._NC.Vehicle.Workshop;

/// <summary>Only the addressed mechanic receives measurements; opening a BUI never exposes them to other observers.</summary>
public sealed partial class NCVehicleWorkshopClientSystem : EntitySystem
{
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    private NCVehicleWorkshopSnapshot? _latest;

    public override void Initialize()
    {
        SubscribeNetworkEvent<NCVehicleWorkshopSnapshot>(OnSnapshot);
    }

    private void OnSnapshot(NCVehicleWorkshopSnapshot snapshot)
    {
        // A private message can arrive before the replicated BUI has created its window.
        _latest = snapshot;
        if (TryGetEntity(snapshot.Vehicle, out var uid) &&
            _ui.TryGetOpenUi<NCVehicleServiceBoundUserInterface>(uid.Value, NCVehicleUiKey.Service, out var bui))
            bui.ApplySnapshot(snapshot);
    }

    public void TryApplyLatest(EntityUid uid)
    {
        if (_latest is { } snapshot && GetNetEntity(uid) == snapshot.Vehicle &&
            _ui.TryGetOpenUi<NCVehicleServiceBoundUserInterface>(uid, NCVehicleUiKey.Service, out var bui))
            bui.ApplySnapshot(snapshot);
    }
}
