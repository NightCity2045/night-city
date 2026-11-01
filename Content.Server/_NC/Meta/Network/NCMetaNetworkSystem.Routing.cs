// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Devices;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetworkSystem
{
    /// <summary>Native invalidation signal; subscribers queue bounded work rather than querying routes in the callback.</summary>
    public event Action? RoutingChanged;

    private void InitializeRoutingNotifications()
    {
        _devices.Changed += OnRoutingDeviceChanged;
        SubscribeLocalEvent<NCMetaPortComponent, MoveEvent>(OnRoutingPortMoved);
        SubscribeLocalEvent<NCMetaRadioInterferenceComponent, MoveEvent>(OnRoutingInterferenceMoved);
    }
    private void OnRoutingDeviceChanged(NCMetaDeviceEntry entry) => RoutingChanged?.Invoke();
    private void OnRoutingPortMoved(Entity<NCMetaPortComponent> ent, ref MoveEvent args) => RoutingChanged?.Invoke();
    private void OnRoutingInterferenceMoved(Entity<NCMetaRadioInterferenceComponent> ent, ref MoveEvent args) => RoutingChanged?.Invoke();
}
