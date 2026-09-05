// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Botany.Components;
using Content.Server.Cargo.Components;
using Content.Server.Chemistry.Components;
using Content.Server.Kitchen.Components;
using Content.Server.Research.Components;
using Content.Server.Research.Systems;
using Content.Server.Station.Systems;
using Content.Shared.Cargo.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Fluids.Components;
using Content.Shared.Research.Components;
using Content.Shared.Storage;
using Content.Shared._NC.LivingCity.Neural;

namespace Content.Server._NC.LivingCity.Jobs;

/// <summary>
/// Evaluates one already-perceived provider against a closed station-work predicate. It never scans the world and
/// never chooses an intent; the opportunity bus uses the result only to describe real work to one GPT actor.
/// </summary>
public sealed partial class NCStationWorkOpportunityProbeSystem : EntitySystem
{
    [Dependency] private StationSystem _stations = default!;
    [Dependency] private ResearchSystem _research = default!;
    [Dependency] private DamageableSystem _damageable = default!;

    public bool IsAvailable(NCStationWorkOpportunitySource source, EntityUid provider) => source switch
    {
        NCStationWorkOpportunitySource.PendingCargoOrder => HasPendingCargoOrder(provider),
        NCStationWorkOpportunitySource.ReagentDispenser =>
            HasComp<ReagentDispenserComponent>(provider) &&
            TryComp<StorageComponent>(provider, out var storage) && storage.StoredItems.Count > 0,
        NCStationWorkOpportunitySource.Microwave => HasComp<MicrowaveComponent>(provider) &&
                                                     !HasComp<ActiveMicrowaveComponent>(provider),
        NCStationWorkOpportunitySource.HarvestablePlant =>
            TryComp<PlantHolderComponent>(provider, out var plant) && plant.Harvest && !plant.Dead,
        NCStationWorkOpportunitySource.UnlockableResearch => HasUnlockableResearch(provider),
        NCStationWorkOpportunitySource.DirtyPuddle => HasComp<PuddleComponent>(provider),
        NCStationWorkOpportunitySource.DamagedEntity =>
            TryComp<DamageableComponent>(provider, out var damageable) &&
            _damageable.GetTotalDamage((provider, damageable)) > 0,
        _ => false,
    };

    private bool HasPendingCargoOrder(EntityUid console)
    {
        if (!TryComp<CargoOrderConsoleComponent>(console, out var cargoConsole) ||
            _stations.GetOwningStation(console) is not { } station ||
            !TryComp<StationCargoOrderDatabaseComponent>(station, out var database) ||
            !database.Orders.TryGetValue(cargoConsole.Account, out var orders))
        {
            return false;
        }

        return orders.Any(order => !order.Approved);
    }

    private bool HasUnlockableResearch(EntityUid console)
    {
        if (!HasComp<ResearchConsoleComponent>(console) || !HasComp<ResearchClientComponent>(console))
            return false;

        return _research.GetAvailableTechnologies(console)
            .Any(technology => _research.CanServerUnlockTechnology(console, technology));
    }
}
