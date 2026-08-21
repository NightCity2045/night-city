// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.LivingCity.Fixers;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCFixerHandoffComponent : Component
{
    [DataField, AutoNetworkedField] public NCFixerDealId DealId;
    [DataField, AutoNetworkedField] public Guid LegId;
    [DataField, AutoNetworkedField] public NCZoneId ZoneId;
    [DataField, AutoNetworkedField] public bool Hidden;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCNightMarketVenueComponent : Component
{
    [DataField, AutoNetworkedField] public NCNightMarketId NightMarketId;
    [DataField, AutoNetworkedField] public bool Open;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCNightMarketVendorStallComponent : Component
{
    [DataField, AutoNetworkedField] public NCNightMarketId NightMarketId;
    [DataField, AutoNetworkedField] public NCPartyRef Vendor;
}
