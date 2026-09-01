// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Population;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Console;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.LivingCity.Actors.Commands;

/// <summary>
/// Lists the authored residents whose persistent identity grants the full Narrative Actor cognition profile.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCActorListCommand : IConsoleCommand
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "ncactorlist";
    public string Description => Loc.GetString("cmd-ncactorlist-description");
    public string Help => Loc.GetString("cmd-ncactorlist-help", ("command", Command));

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }

        var filter = args.Length == 1 ? args[0].Trim() : string.Empty;
        var catalog = NCNarrativeActorCommandHelpers.BuildCatalog(_prototypes)
            .Where(entry => filter.Length == 0 ||
                            entry.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            entry.QualifiedKey.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            entry.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var actors = _entities.EntitySysManager.GetEntitySystem<NCCityActorRegistrySystem>();
        var loaded = new List<(NCNarrativeActorCommandEntry Entry, NCCityActorRecord? Record)>(catalog.Length);
        foreach (var entry in catalog)
            loaded.Add((entry, await actors.GetAsync(entry.ActorId)));

        // Registry loads may finish off-thread. Runtime bindings and console output stay on the simulation thread.
        _taskManager.RunOnMainThread(() => WriteResults(shell, loaded));
    }

    private void WriteResults(
        IConsoleShell shell,
        IReadOnlyList<(NCNarrativeActorCommandEntry Entry, NCCityActorRecord? Record)> loaded)
    {
        var runtime = _entities.EntitySysManager.GetEntitySystem<NCCityActorRuntimeIndexSystem>();
        shell.WriteLine(Loc.GetString("cmd-ncactorlist-count", ("count", loaded.Count)));
        foreach (var (entry, record) in loaded)
        {
            var status = record == null
                ? Loc.GetString("cmd-ncactor-status-not-imported")
                : record.Snapshot.Identity.NarrativeActorProfileId != entry.NarrativeProfile.Id
                    ? Loc.GetString("cmd-ncactor-status-profile-mismatch")
                    : runtime.TryGetEntity(entry.ActorId, out var entity) && _entities.EntityExists(entity)
                        ? Loc.GetString("cmd-ncactor-status-physical", ("entity", entity))
                        : Loc.GetString("cmd-ncactor-status-abstract");
            shell.WriteLine(Loc.GetString(
                "cmd-ncactorlist-entry",
                ("key", entry.Key),
                ("name", entry.FullName),
                ("profile", entry.NarrativeProfile.Id),
                ("actor", entry.ActorId.Value),
                ("status", status)));
        }

        NCNarrativeActorCommandHelpers.WriteNeuralStatus(
            shell,
            _entities.EntitySysManager.GetEntitySystem<NCNeuralCognitionClientSystem>());
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => args.Length == 1
        ? CompletionResult.FromHintOptions(
            NCNarrativeActorCommandHelpers.GetCompletionKeys(_prototypes),
            Loc.GetString("cmd-ncactor-hint-key"))
        : CompletionResult.Empty;
}

/// <summary>
/// Ensures an existing persistent Narrative Actor is physical beside an administrator-selected player.
/// It never creates an ad-hoc identity or enters the removed abstract materialization pipeline.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCActorSpawnCommand : IConsoleCommand
{
    private const int CompletionPollMilliseconds = 250;
    private const int MaximumCompletionPolls = 2400;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;

    public string Command => "ncactorspawn";
    public string Description => Loc.GetString("cmd-ncactorspawn-description");
    public string Help => Loc.GetString("cmd-ncactorspawn-help", ("command", Command));

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (!NCNarrativeActorCommandHelpers.TryResolve(_prototypes, args[0], out var entry, out var ambiguous))
        {
            shell.WriteError(Loc.GetString(ambiguous
                ? "cmd-ncactor-error-ambiguous"
                : "cmd-ncactor-error-unknown", ("key", args[0])));
            return;
        }

        if (!TryResolveTarget(shell, args, out var target))
            return;

        var actors = _entities.EntitySysManager.GetEntitySystem<NCCityActorRegistrySystem>();
        var actor = await actors.GetAsync(entry.ActorId, true);
        // Coordinate, zone, binding and materialization APIs are simulation-thread authorities.
        _taskManager.RunOnMainThread(() => ContinueAfterLoad(shell, target, entry, actor));
    }

    private bool TryResolveTarget(IConsoleShell shell, string[] args, out ICommonSession target)
    {
        if (args.Length == 2)
        {
            if (_players.TryGetSessionByUsername(args[1], out target!))
                return true;

            shell.WriteError(Loc.GetString("cmd-ncactor-error-player-offline", ("player", args[1])));
            target = default!;
            return false;
        }

        if (shell.Player is { } player)
        {
            target = player;
            return true;
        }

        shell.WriteError(Loc.GetString("cmd-ncactor-error-server-needs-player"));
        target = default!;
        return false;
    }

    private void ContinueAfterLoad(
        IConsoleShell shell,
        ICommonSession target,
        NCNarrativeActorCommandEntry entry,
        NCCityActorRecord? actor)
    {
        if (actor == null)
        {
            shell.WriteError(Loc.GetString("cmd-ncactor-error-not-imported", ("key", entry.Key)));
            return;
        }

        if (actor.Snapshot.Identity.NarrativeActorProfileId != entry.NarrativeProfile.Id)
        {
            shell.WriteError(Loc.GetString("cmd-ncactor-error-profile-mismatch", ("key", entry.Key)));
            return;
        }

        var systems = _entities.EntitySysManager;
        if (target.AttachedEntity is not { } targetEntity || !_entities.EntityExists(targetEntity))
        {
            shell.WriteError(Loc.GetString("cmd-ncactor-error-player-no-entity", ("player", target.Name)));
            return;
        }

        var coordinates = systems.GetEntitySystem<NCMapCoordinatesSystem>();
        if (!coordinates.TryGetCoordinates(targetEntity, out var targetCoordinates))
        {
            shell.WriteError(Loc.GetString("cmd-ncactor-error-no-city-coordinates", ("player", target.Name)));
            return;
        }

        var requestId = Guid.NewGuid();
        var lifecycle = systems.GetEntitySystem<NCPhysicalActorLifecycleSystem>();
        var state = lifecycle.RequestAdmin(
            requestId,
            entry.ActorId,
            targetCoordinates,
            NCNarrativeActorCommandHelpers.AuditSource(shell, "spawn"));
        if (state.Status == NCPhysicalActorSpawnStatus.Rejected)
        {
            shell.WriteError(Loc.GetString(
                "cmd-ncactor-error-materialization",
                ("key", entry.Key),
                ("reason", state.Error ?? Loc.GetString("cmd-ncactor-error-unknown-reason"))));
            return;
        }

        shell.WriteLine(Loc.GetString(
            "cmd-ncactorspawn-requested",
            ("key", entry.Key),
            ("name", entry.FullName),
            ("actor", entry.ActorId.Value),
            ("request", state.RequestId),
            ("status", NCNarrativeActorCommandHelpers.PhysicalSpawnStatus(state.Status))));
        NCNarrativeActorCommandHelpers.WriteNeuralStatus(
            shell,
            _entities.EntitySysManager.GetEntitySystem<NCNeuralCognitionClientSystem>());
        ObserveCompletion(shell, entry, state.RequestId, 0);
    }

    /// <summary>
    /// Reports the terminal admin result instead of leaving the initial queued message as the only feedback.
    /// Timer callbacks run on the simulation synchronization context, so lifecycle state stays thread-safe.
    /// </summary>
    private void ObserveCompletion(
        IConsoleShell shell,
        NCNarrativeActorCommandEntry entry,
        Guid requestId,
        int polls)
    {
        Timer.Spawn(CompletionPollMilliseconds, () =>
        {
            var systems = _entities.EntitySysManager;
            var lifecycle = systems.GetEntitySystem<NCPhysicalActorLifecycleSystem>();
            if (!lifecycle.TryGetRequest(requestId, out var state) || polls >= MaximumCompletionPolls)
            {
                shell.WriteError(Loc.GetString(
                    "cmd-ncactorspawn-result-unavailable",
                    ("key", entry.Key),
                    ("request", requestId)));
                return;
            }

            if (state.Status is NCPhysicalActorSpawnStatus.Queued or
                NCPhysicalActorSpawnStatus.Loading or
                NCPhysicalActorSpawnStatus.Retrying)
            {
                ObserveCompletion(shell, entry, requestId, polls + 1);
                return;
            }

            var status = NCNarrativeActorCommandHelpers.PhysicalSpawnStatus(state.Status);
            if (state.Status is NCPhysicalActorSpawnStatus.Spawned or
                NCPhysicalActorSpawnStatus.AlreadyPhysical)
            {
                shell.WriteLine(Loc.GetString(
                    "cmd-ncactorspawn-completed",
                    ("key", entry.Key),
                    ("request", requestId),
                    ("status", status),
                    ("entity", state.EntityUid?.ToString() ?? "none")));
                return;
            }

            shell.WriteError(Loc.GetString(
                "cmd-ncactorspawn-failed",
                ("key", entry.Key),
                ("request", requestId),
                ("status", status),
                ("reason", state.Error ?? Loc.GetString("cmd-ncactor-error-unknown-reason"))));
        });
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args) => args.Length switch
    {
        1 => CompletionResult.FromHintOptions(
            NCNarrativeActorCommandHelpers.GetCompletionKeys(_prototypes),
            Loc.GetString("cmd-ncactor-hint-key")),
        2 => CompletionResult.FromHintOptions(
            CompletionHelper.SessionNames(players: _players),
            Loc.GetString("cmd-ncactor-hint-player")),
        _ => CompletionResult.Empty,
    };
}

internal sealed record NCNarrativeActorCommandEntry(
    string Key,
    string QualifiedKey,
    string FullName,
    ProtoId<NCNarrativeActorProfilePrototype> NarrativeProfile,
    NCActorId ActorId);

/// <summary>
/// Pure prototype catalog helpers shared by the three cold-path administration commands.
/// </summary>
internal static class NCNarrativeActorCommandHelpers
{
    public static IReadOnlyList<NCNarrativeActorCommandEntry> BuildCatalog(IPrototypeManager prototypes)
    {
        var result = new List<NCNarrativeActorCommandEntry>();
        foreach (var population in prototypes.EnumeratePrototypes<NCCityPopulationProfilePrototype>()
                     .OrderBy(static profile => profile.ID, StringComparer.Ordinal))
        {
            foreach (var actor in population.Actors)
            {
                if (actor.NarrativeActorProfile is not { } narrativeId ||
                    !prototypes.TryIndex(narrativeId, out NCNarrativeActorProfilePrototype? narrative) ||
                    narrative == null || !TryGetPersistentLocalId(population, actor, out var persistentLocalId))
                {
                    continue;
                }

                var key = narrative.StableKey.Trim();
                result.Add(new NCNarrativeActorCommandEntry(
                    key,
                    $"{population.ID}:{key}",
                    actor.FullName.Trim(),
                    narrativeId,
                    NCCityActorRegistrySystem.CreateDeterministicId(population.ScopeId, persistentLocalId)));
            }
        }

        result.Sort(static (left, right) =>
        {
            var key = string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
            return key != 0
                ? key
                : string.Compare(left.QualifiedKey, right.QualifiedKey, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    public static bool TryResolve(
        IPrototypeManager prototypes,
        string value,
        out NCNarrativeActorCommandEntry entry,
        out bool ambiguous)
    {
        entry = default!;
        ambiguous = false;
        var key = value.Trim();
        var id = Guid.TryParse(key, out var rawId) && rawId != Guid.Empty ? new NCActorId(rawId) : default;
        var matches = BuildCatalog(prototypes)
            .Where(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.QualifiedKey, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.FullName, key, StringComparison.OrdinalIgnoreCase) ||
                candidate.ActorId == id)
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            ambiguous = matches.Length > 1;
            return false;
        }

        entry = matches[0];
        return true;
    }

    public static IEnumerable<string> GetCompletionKeys(IPrototypeManager prototypes)
    {
        var catalog = BuildCatalog(prototypes);
        var duplicates = catalog.GroupBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog.Select(entry => duplicates.Contains(entry.Key) ? entry.QualifiedKey : entry.Key);
    }

    public static string AuditSource(IConsoleShell shell, string operation)
    {
        return shell.Player is { } player
            ? $"admin:console:{operation}:{player.UserId}"
            : $"admin:console:{operation}:server";
    }

    public static void WriteNeuralStatus(IConsoleShell shell, NCNeuralCognitionClientSystem neural)
    {
        var state = neural.GetRuntimeSnapshot();
        var mode = NeuralMode(state.BudgetMode);
        if (neural.IsProviderEnabled && state.BudgetMode != NCNeuralBudgetMode.FallbackOnly)
        {
            shell.WriteLine(Loc.GetString("cmd-ncactor-neural-ready", ("mode", mode)));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-ncactor-neural-fallback", ("mode", mode)));
    }

    public static string PhysicalSpawnStatus(NCPhysicalActorSpawnStatus status) => Loc.GetString(status switch
    {
        NCPhysicalActorSpawnStatus.Queued => "cmd-ncactor-spawn-status-queued",
        NCPhysicalActorSpawnStatus.Loading => "cmd-ncactor-spawn-status-loading",
        NCPhysicalActorSpawnStatus.Retrying => "cmd-ncactor-spawn-status-retrying",
        NCPhysicalActorSpawnStatus.Spawned => "cmd-ncactor-spawn-status-spawned",
        NCPhysicalActorSpawnStatus.AlreadyPhysical => "cmd-ncactor-spawn-status-already",
        NCPhysicalActorSpawnStatus.Rejected => "cmd-ncactor-spawn-status-rejected",
        _ => "cmd-ncactor-status-unknown",
    });

    private static string NeuralMode(NCNeuralBudgetMode mode) => Loc.GetString(mode switch
    {
        NCNeuralBudgetMode.Normal => "cmd-ncactor-neural-mode-normal",
        NCNeuralBudgetMode.Economy => "cmd-ncactor-neural-mode-economy",
        NCNeuralBudgetMode.Restricted => "cmd-ncactor-neural-mode-restricted",
        NCNeuralBudgetMode.FallbackOnly => "cmd-ncactor-neural-mode-fallback",
        _ => "cmd-ncactor-status-unknown",
    });

    private static bool TryGetPersistentLocalId(
        NCCityPopulationProfilePrototype population,
        NCCityPopulationActorEntry actor,
        out string persistentLocalId)
    {
        var replacementRequested = !string.IsNullOrWhiteSpace(actor.ReplacesCohortId) ||
                                   actor.ReplacesCohortIndex >= 0;
        if (!replacementRequested)
        {
            persistentLocalId = NCCityPopulationImportSystem.BuildAuthoredStableLocalId(
                population.ID,
                actor.StableLocalId);
            return true;
        }

        var cohort = population.Cohorts.FirstOrDefault(candidate =>
            candidate.Id == actor.ReplacesCohortId &&
            actor.ReplacesCohortIndex >= 0 &&
            actor.ReplacesCohortIndex < candidate.Count);
        if (cohort == null)
        {
            persistentLocalId = string.Empty;
            return false;
        }

        persistentLocalId = NCCityPopulationImportSystem.BuildCohortStableLocalId(
            population.ID,
            cohort.Id,
            actor.ReplacesCohortIndex);
        return true;
    }
}
