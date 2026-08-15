// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Content.Server.Administration;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Population.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityPopulationStatusCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityPopulationImportSystem _population = default!;

    public override string Command => "alife_population_status";
    public override string Description => "Show the bounded persistent-population import state.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var progress = _population.Progress;
        shell.WriteLine(
            $"Population import: running={progress.Running}, profile={progress.ProfileId ?? "none"}, " +
            $"processed={progress.Processed}/{progress.Requested}, created={progress.Created}, " +
            $"existing={progress.Existing}, conflicts={progress.Conflicts}, failed={progress.Failed}.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityPopulationValidateCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityPopulationImportSystem _population = default!;

    public override string Command => "alife_population_validate";
    public override string Description => "Validate one data-driven persistent population profile.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError($"Usage: {Command} <populationProfile>");
            return;
        }

        var errors = new List<string>();
        var profile = new ProtoId<NCCityPopulationProfilePrototype>(args[0]);
        if (_population.ValidateProfile(profile, errors))
        {
            shell.WriteLine($"Population profile {profile} is valid.");
            return;
        }

        foreach (var error in errors)
            shell.WriteError(error);
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityPopulationImportCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityPopulationImportSystem _population = default!;

    public override string Command => "alife_population_import";
    public override string Description => "Idempotently import one persistent population profile during an active round.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError($"Usage: {Command} <populationProfile>");
            return;
        }

        var report = await _population.ImportAsync(
            new ProtoId<NCCityPopulationProfilePrototype>(args[0]),
            shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console");
        var summary =
            $"{report.Status}: profile={report.ProfileId}, processed={report.Created + report.Existing + report.Conflicts + report.Failed}/" +
            $"{report.Requested}, created={report.Created}, existing={report.Existing}, " +
            $"conflicts={report.Conflicts}, failed={report.Failed}.";
        if (report.Status == NCCityPopulationImportStatus.Completed && report.Failed == 0 && report.Conflicts == 0)
            shell.WriteLine(summary);
        else
            shell.WriteError(summary);

        foreach (var failure in report.Failures.Take(20))
            shell.WriteError($"{failure.StableLocalId}: {failure.Error}");
        if (report.Failures.Count > 20)
            shell.WriteError($"... {report.Failures.Count - 20} additional failures omitted.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityZoneStateInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityZoneStateRegistrySystem _registry = default!;

    public override string Command => "alife_zone_state";
    public override string Description => "Inspect one persistent district/sector population and city-state projection.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !NCZoneId.TryParse(args[0], out var scopeZone))
        {
            shell.WriteError($"Usage: {Command} <districtOrSectorZoneId>");
            return;
        }

        var record = await _registry.GetAsync(scopeZone, true);
        if (record == null)
        {
            shell.WriteError($"Zone-state projection {scopeZone} was not found or is unreadable.");
            return;
        }

        var snapshot = record.Snapshot;
        var population = snapshot.Population;
        shell.WriteLine(
            $"Zone state: zone={scopeZone}, slot={snapshot.ScopeSlot}, version={record.AggregateVersion}, " +
            $"captured={snapshot.CapturedAt.Elapsed}.");
        shell.WriteLine(
            $"Population: total={population.TotalActors}, living={population.LivingActors}, " +
            $"dead={population.DeadActors}, frozen={population.FrozenActors}, " +
            $"traveling={population.TravelingActors}, activityUnits={population.WeightedActivityUnits:0.###}.");
        shell.WriteLine(
            $"Buckets: statuses={population.Statuses.Length}, cognition={population.CognitionTiers.Length}, " +
            $"availability={population.Availability.Length}, archetypes={population.Archetypes.Length}.");
        foreach (var signal in snapshot.Signals.OrderBy(value => value.Kind))
        {
            shell.WriteLine(
                $"Signal {signal.Kind}: availability={signal.Availability}, value={signal.Value:0.###}, " +
                $"source={signal.Source}@{signal.SourceVersion}.");
        }
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityZoneStateRebuildCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityZoneStateProjectionSystem _projections = default!;

    public override string Command => "alife_zone_state_rebuild";
    public override string Description => "Restart the bounded active-round district/sector projection rebuild.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        if (!_projections.ForceRebuild())
        {
            shell.WriteError("Zone-state rebuild requires an active A-Life round.");
            return;
        }

        shell.WriteLine("The bounded zone-state rebuild has started.");
    }
}
