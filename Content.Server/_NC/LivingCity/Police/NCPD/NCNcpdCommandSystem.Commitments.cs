// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Police.NCPD;

public sealed partial class NCNcpdCommandSystem
{
    private const int MaximumCommitmentMutationAttempts = 3;
    private const int MaximumCommitmentTextLength = 128;

    [Dependency] private NCCityActorRegistrySystem _actorRegistry = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly Dictionary<NCNcpdCommandId, Dictionary<NCActorId, NCCityCommitmentId>>
        _commitmentsByCommand = [];
    private readonly HashSet<NCNcpdCommandId> _commitmentProjectionsInFlight = [];

    public int ActiveCommitmentCount => _commitmentsByCommand.Values.Sum(static value => value.Count);
    public int CommitmentProjectionsInFlight => _commitmentProjectionsInFlight.Count;
    public long CommitmentProjectionFailures { get; private set; }
    public string? LastCommitmentError { get; private set; }

    private static bool CanProjectCommitments(NCNcpdCommandPolicyPrototype policy) =>
        policy.CommitmentDuration > TimeSpan.Zero && policy.CommitmentDuration <= TimeSpan.FromHours(24);

    private void QueueCommandCommitments(
        NCNcpdCommandRecord command,
        NCNcpdUnitSnapshot unit,
        NCNcpdCommandPolicyPrototype policy)
    {
        if (!_commitmentProjectionsInFlight.Add(command.CommandId))
            return;

        // UI and automatic dispatch both use the same actor-root commitment API as spoken directives.
        _ = PersistCommandCommitmentsAsync(command, unit.Members, policy.CommitmentDuration);
    }

    private async Task PersistCommandCommitmentsAsync(
        NCNcpdCommandRecord command,
        IReadOnlyList<NCActorId> members,
        TimeSpan duration)
    {
        var projected = new Dictionary<NCActorId, NCCityCommitmentId>();
        string? error = null;
        try
        {
            foreach (var actorId in members.Distinct())
            {
                var commitmentId = NCPersistentIdentity.NcpdCommandCommitment(command.CommandId, actorId);
                if (!await TryAddCommandCommitmentAsync(command, actorId, commitmentId, duration))
                {
                    error = $"Could not persist the NCPD order commitment for actor {actorId}.";
                    break;
                }
                projected.Add(actorId, commitmentId);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        _taskManager.RunOnMainThread(() => FinishCommandCommitmentProjection(command, projected, error));
    }

    private async Task<bool> TryAddCommandCommitmentAsync(
        NCNcpdCommandRecord command,
        NCActorId actorId,
        NCCityCommitmentId commitmentId,
        TimeSpan duration)
    {
        for (var attempt = 0; attempt < MaximumCommitmentMutationAttempts; attempt++)
        {
            var actor = await _actorRegistry.GetAsync(actorId, true);
            if (actor == null)
                return false;
            if (actor.Snapshot.Schedule.Commitments.Any(value => value.CommitmentId == commitmentId))
                return true;

            var stable = $"{command.CommandId}.{actorId}.{attempt}";
            var result = await _actorRegistry.AddCommitmentAsync(new NCCityActorAddCommitmentRequest(
                NCPersistentIdentity.Command("ncpd-order", "add-commitment", stable),
                actorId,
                actor.AggregateVersion,
                new NCCityActorCommitment(
                    commitmentId,
                    CommitmentActivity(command.Kind),
                    NormalizeCommitmentText(command.Reason),
                    command.TargetZone,
                    command.IssuedAt,
                    command.IssuedAt.Advance(duration),
                    NCCityActorAvailability.Busy,
                    command.Priority),
                NCPersistentIdentity.Correlation("ncpd-order", "commitment", command.CommandId.ToString()),
                command.CommandId.Value,
                "ncpd-command"));
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                return true;
            if (result.Status != NCCityActorMutationStatus.VersionConflict)
                return false;
        }
        return false;
    }

    private void FinishCommandCommitmentProjection(
        NCNcpdCommandRecord command,
        Dictionary<NCActorId, NCCityCommitmentId> projected,
        string? error)
    {
        _commitmentProjectionsInFlight.Remove(command.CommandId);
        var stillActive = _activeByUnit.TryGetValue(command.UnitId, out var activeId) &&
                          activeId == command.CommandId;
        if (error == null && stillActive)
        {
            _commitmentsByCommand[command.CommandId] = projected;
            LastCommitmentError = null;
            return;
        }

        if (projected.Count > 0)
            _ = RemoveCommandCommitmentsAsync(command.CommandId, projected);
        if (error == null)
            return;

        CommitmentProjectionFailures++;
        LastCommitmentError = error;
        if (stillActive)
            TryAdvanceExecution(command.CommandId, NCNcpdCommandStatus.Failed, error);
    }

    private void QueueReleaseCommandCommitments(NCNcpdCommandId commandId)
    {
        if (_commitmentsByCommand.Remove(commandId, out var commitments))
            _ = RemoveCommandCommitmentsAsync(commandId, commitments);
    }

    private void QueueReleaseAllCommandCommitments()
    {
        foreach (var commandId in _commitmentsByCommand.Keys.ToArray())
            QueueReleaseCommandCommitments(commandId);
    }

    private async Task RemoveCommandCommitmentsAsync(
        NCNcpdCommandId commandId,
        IReadOnlyDictionary<NCActorId, NCCityCommitmentId> commitments)
    {
        foreach (var (actorId, commitmentId) in commitments)
        {
            var removed = false;
            for (var attempt = 0; attempt < MaximumCommitmentMutationAttempts; attempt++)
            {
                var actor = await _actorRegistry.GetAsync(actorId, true);
                if (actor == null || actor.Snapshot.Schedule.Commitments.All(value =>
                        value.CommitmentId != commitmentId))
                {
                    removed = true;
                    break;
                }

                var stable = $"{commandId}.{actorId}.{attempt}";
                var result = await _actorRegistry.RemoveCommitmentAsync(new NCCityActorRemoveCommitmentRequest(
                    NCPersistentIdentity.Command("ncpd-order", "remove-commitment", stable),
                    actorId,
                    actor.AggregateVersion,
                    commitmentId,
                    NCPersistentIdentity.Correlation("ncpd-order", "commitment", commandId.ToString()),
                    commandId.Value,
                    "ncpd-command"));
                if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                {
                    removed = true;
                    break;
                }
                if (result.Status != NCCityActorMutationStatus.VersionConflict)
                    break;
            }

            if (!removed)
            {
                _taskManager.RunOnMainThread(() =>
                {
                    CommitmentProjectionFailures++;
                    LastCommitmentError = $"Could not remove the NCPD order commitment for actor {actorId}.";
                });
            }
        }
    }

    private static string CommitmentActivity(NCNcpdCommandKind kind) => kind switch
    {
        NCNcpdCommandKind.PatrolZone => "patrol-location",
        NCNcpdCommandKind.CoverZone or NCNcpdCommandKind.HoldZone => "guard-location",
        _ => "report-to-location",
    };

    private static string NormalizeCommitmentText(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?) null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized[..Math.Min(normalized.Length, MaximumCommitmentTextLength)];
    }
}
