// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server.Botany.Components;
using Content.Server.Botany.Systems;
using Content.Server.Cargo.Components;
using Content.Server.Chemistry.Components;
using Content.Server.Kitchen.Components;
using Content.Server.Kitchen.EntitySystems;
using Content.Server.Research.Components;
using Content.Server.Research.Systems;
using Content.Server.Station.Systems;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Research.Components;
using Content.Shared.Storage;
using Content.Shared._NC.LivingCity.Cognition;

namespace Content.Server._NC.LivingCity.Jobs;

/// <summary>
/// Executes the small set of station-job operations that normally enter through a player BUI. Every mutation is
/// delegated to the unchanged cargo, research, chemistry, kitchen or botany authority and verified afterwards.
/// </summary>
public sealed partial class NCStationWorkActionAdapterSystem : EntitySystem
{
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private StationSystem _stations = default!;
    [Dependency] private ResearchSystem _research = default!;
    [Dependency] private MicrowaveSystem _microwave = default!;
    [Dependency] private PlantHolderSystem _plants = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (args.Action.Executor != NCHumanActionExecutorKind.VanillaStationWork)
            return;

        args.Handled = true;
        if (args.EntityUid is not { } actor || !Exists(actor) ||
            !TryResolveTarget(args, out var target) || target == actor ||
            args.Action.MaximumRange > 0f &&
            !_interaction.InRangeUnobstructed(actor, target, args.Action.MaximumRange))
        {
            args.Failure = "station-work-target-unavailable";
            return;
        }

        switch (args.Action.Operation)
        {
            case NCHumanActionOperationKind.ProcessCargoOrder:
                TryProcessCargoOrder(actor, target, args);
                return;
            case NCHumanActionOperationKind.UnlockResearchTechnology:
                TryUnlockResearch(actor, target, args);
                return;
            case NCHumanActionOperationKind.PrepareDispensedMixture:
                TryPrepareDispensedMixture(actor, target, args);
                return;
            case NCHumanActionOperationKind.StartMicrowave:
                TryStartMicrowave(actor, target, args);
                return;
            case NCHumanActionOperationKind.HarvestPlant:
                TryHarvestPlant(actor, target, args);
                return;
            default:
                args.Failure = $"station-work-operation-unhandled:{args.Action.Operation}";
                return;
        }
    }

    private void TryProcessCargoOrder(EntityUid actor, EntityUid target, NCHumanActionExecutionEvent args)
    {
        if (!TryComp<CargoOrderConsoleComponent>(target, out var console) ||
            _stations.GetOwningStation(target) is not { } station ||
            !TryComp<StationCargoOrderDatabaseComponent>(station, out var database) ||
            !database.Orders.TryGetValue(console.Account, out var orders) ||
            orders.FirstOrDefault(order => !order.Approved) is not { } order)
        {
            args.Failure = "station-work-cargo-order-unavailable";
            return;
        }

        var orderId = order.OrderId;
        var message = new CargoConsoleApproveOrderMessage(orderId) { Actor = actor };
        RaiseLocalEvent(target, message);
        if (orders.All(candidate => candidate.OrderId != orderId || candidate.Approved))
            args.Completed = true;
        else
            args.Failure = "station-work-cargo-order-rejected";
    }

    private void TryUnlockResearch(EntityUid actor, EntityUid target, NCHumanActionExecutionEvent args)
    {
        if (!HasComp<ResearchConsoleComponent>(target) || !HasComp<ResearchClientComponent>(target))
        {
            args.Failure = "station-work-research-console-unavailable";
            return;
        }

        var technology = _research.GetAvailableTechnologies(target)
            .Where(candidate => _research.CanServerUnlockTechnology(target, candidate))
            .OrderBy(candidate => candidate.Cost)
            .ThenBy(candidate => candidate.ID, StringComparer.Ordinal)
            .FirstOrDefault();
        if (technology == null || !_research.UnlockTechnology(target, technology, actor))
        {
            args.Failure = "station-work-research-unlock-rejected";
            return;
        }

        args.Completed = true;
    }

    private void TryPrepareDispensedMixture(EntityUid actor, EntityUid target, NCHumanActionExecutionEvent args)
    {
        if (!HasComp<ReagentDispenserComponent>(target) ||
            !TryComp<StorageComponent>(target, out var storage) || storage.StoredItems.Count == 0 ||
            _itemSlots.GetItemOrNull(target, SharedReagentDispenser.OutputSlotName) is not { } output ||
            !_solutions.TryGetFitsInDispenser(output, out _, out var outputSolution))
        {
            args.Failure = "station-work-dispenser-output-unavailable";
            return;
        }

        var before = outputSolution.Volume;
        var amount = new ReagentDispenserSetDispenseAmountMessage(ReagentDispenserDispenseAmount.U10)
            { Actor = actor };
        RaiseLocalEvent(target, amount);
        foreach (var location in storage.StoredItems.Values.Take(2).ToArray())
        {
            var dispense = new ReagentDispenserDispenseReagentMessage(location) { Actor = actor };
            RaiseLocalEvent(target, dispense);
        }

        if (!_solutions.TryGetFitsInDispenser(output, out _, out outputSolution) || outputSolution.Volume <= before)
        {
            args.Failure = "station-work-dispenser-transfer-rejected";
            return;
        }

        args.Completed = true;
    }

    private void TryStartMicrowave(EntityUid actor, EntityUid target, NCHumanActionExecutionEvent args)
    {
        if (!TryComp<MicrowaveComponent>(target, out var microwave) ||
            HasComp<ActiveMicrowaveComponent>(target) || !MicrowaveSystem.HasContents(microwave))
        {
            args.Failure = "station-work-microwave-not-ready";
            return;
        }

        _microwave.Wzhzhzh(target, microwave, actor);
        if (HasComp<ActiveMicrowaveComponent>(target))
            args.Completed = true;
        else
            args.Failure = "station-work-microwave-start-rejected";
    }

    private void TryHarvestPlant(EntityUid actor, EntityUid target, NCHumanActionExecutionEvent args)
    {
        if (!TryComp<PlantHolderComponent>(target, out var plant) || !plant.Harvest || plant.Dead ||
            !_plants.DoHarvest(target, actor, plant))
        {
            args.Failure = "station-work-plant-harvest-rejected";
            return;
        }

        args.Completed = true;
    }

    private bool TryResolveTarget(NCHumanActionExecutionEvent args, out EntityUid target)
    {
        target = EntityUid.Invalid;
        return args.Target.Kind == NCCognitionReferenceKind.StableKey &&
               _perception.TryResolveSubjectHandle(args.ActorId, args.Target.StableId, args.Now, out target);
    }
}
