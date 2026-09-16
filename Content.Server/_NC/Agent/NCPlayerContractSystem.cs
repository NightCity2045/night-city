// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Agent;

/// <summary>
/// Durable player promises. This domain deliberately has no bank, escrow, item or reputation dependency.
/// Party projections and the authoritative record commit together, so history survives reconnects and restarts.
/// </summary>
public sealed class NCPlayerContractSystem : EntitySystem
{
    private const string Aggregate = "agent.player-contract";
    private const string PartyAggregate = "agent.player-contract-party";
    private const string AuthorAggregate = "agent.player-contract-author";
    private static readonly ProtoId<NCPlayerContractPolicyPrototype> PolicyId = "Default";
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    // Board mutations are infrequent. Serialize their validation, with database versions as the final authority.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Task> _pending = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_pending.Count > 0) args.AddPendingWrite(Task.WhenAll(_pending.ToArray()));
    }

    private async Task<bool> TrackMutation(Task<bool> task)
    {
        _pending.Add(task);
        try { return await task; }
        finally { _pending.Remove(task); }
    }

    public async Task<(List<NCPlayerContractEntry> Entries, string? Cursor)> GetPageAsync(
        NCCharacterId viewer, bool history, string? cursor)
    {
        var policy = _prototypes.Index(PolicyId);
        var size = Math.Clamp(policy.PageSize, 1, 100);
        if (cursor?.Length > 96 || !_clock.TryGetCurrentTime(out _, out var now))
            return ([], null);
        var rows = history
            ? await _database.GetNCAggregateSnapshotPrefixPageDescendingAsync(PartyAggregate,
                Prefix(viewer.Value), cursor, size)
            : await _database.GetNCAggregateSnapshotPageDescendingAsync(Aggregate, cursor, size);
        var entries = new List<NCPlayerContractEntry>();
        foreach (var row in rows)
        {
            var value = JsonSerializer.Deserialize<ContractRecord>(row.Payload);
            if (value == null || (history && value.Customer != viewer.Value && value.Contractor != viewer.Value))
                continue;
            var status = EffectiveStatus(value, now.Ticks);
            if (!history && status != NCPlayerContractStatus.Open)
                continue;
            entries.Add(new NCPlayerContractEntry(value.Id, value.Version, value.Customer, value.CustomerName,
                value.Contractor, value.ContractorName, value.Title, value.Terms, value.Reward, status,
                value.Deadline is { } deadline ? Math.Max(0, (deadline - now.Ticks) / TimeSpan.TicksPerSecond) : null));
        }
        return (entries, rows.Count == size ? rows[^1].AggregateId : null);
    }

    public Task<bool> TryCreateAsync(EntityUid user, NCAgentContractCreateMessage request) =>
        TrackMutation(TryCreateCoreAsync(user, request));

    private async Task<bool> TryCreateCoreAsync(EntityUid user, NCAgentContractCreateMessage request)
    {
        if (!TryCharacter(user, out var character) || request.CommandId == Guid.Empty)
            return false;
        var policy = _prototypes.Index(PolicyId);
        if (!ValidText(request.Title, policy.MaximumTitleLength) ||
            !ValidText(request.Terms, policy.MaximumTermsLength) ||
            !ValidText(request.Reward, policy.MaximumRewardLength) || request.DeadlineMinutes < 0 ||
            request.DeadlineMinutes > policy.MaximumDeadlineMinutes)
            return false;
        await _gate.WaitAsync();
        try
        {
            if (!TryCharacter(user, out var current) || current != character ||
                !_clock.TryGetCurrentTime(out var epoch, out var now))
                return false;
            var authorKey = character.Value.ToString();
            var receipt = await _database.GetNCPersistentCommandResultAsync(request.CommandId);
            if (receipt != null)
                return receipt.CommandType == Aggregate + ".create" && receipt.ScopeId == authorKey &&
                    receipt.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
            var author = await _database.GetNCAggregateSnapshotAsync(AuthorAggregate, authorKey);
            if (author != null && now.Ticks - JsonSerializer.Deserialize<long>(author.Payload) < policy.PublishCooldown.Ticks)
                return false;
            var id = $"{now.Ticks:D19}-{Guid.NewGuid():N}";
            var record = new ContractRecord(id, 1, character.Value, Name(user), null, string.Empty,
                request.Title.Trim(), request.Terms.Trim(), request.Reward.Trim(), NCPlayerContractStatus.Open,
                request.DeadlineMinutes == 0 ? null : now.Advance(TimeSpan.FromMinutes(request.DeadlineMinutes)).Ticks);
            var writes = new List<NCPersistentAggregateWrite>
            {
                Write(Aggregate, id, 0, record),
                Write(PartyAggregate, Prefix(character.Value) + id, 0, record),
                Write(AuthorAggregate, authorKey, author?.AggregateVersion ?? 0, now.Ticks),
            };
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new(request.CommandId,
                Aggregate + ".create", Aggregate, authorKey, writes, null, request.CommandId, null, epoch, now, []));
            return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
        }
        finally { _gate.Release(); }
    }

    public Task<bool> TryActAsync(EntityUid user, NCAgentContractActionMessage request) =>
        TrackMutation(TryActCoreAsync(user, request));

    private async Task<bool> TryActCoreAsync(EntityUid user, NCAgentContractActionMessage request)
    {
        if (!TryCharacter(user, out var character) || request.Id.Length > 96 || request.Version <= 0)
            return false;
        await _gate.WaitAsync();
        try
        {
            var stored = await _database.GetNCAggregateSnapshotAsync(Aggregate, request.Id);
            if (stored == null || stored.AggregateVersion != request.Version ||
                !TryCharacter(user, out var current) || current != character ||
                !_clock.TryGetCurrentTime(out var epoch, out var now))
                return false;
            var value = JsonSerializer.Deserialize<ContractRecord>(stored.Payload);
            if (value == null || value.Id != request.Id || value.Version != request.Version ||
                !CanChangeStatus(EffectiveStatus(value, now.Ticks), value.Customer, value.Contractor,
                    character.Value, request.Action, out var status))
                return false;
            var proposed = value with
            {
                Version = value.Version + 1, Status = status,
                Contractor = request.Action == NCPlayerContractAction.Accept ? character.Value : value.Contractor,
                ContractorName = request.Action == NCPlayerContractAction.Accept ? Name(user) : value.ContractorName,
            };
            var writes = new List<NCPersistentAggregateWrite> { Write(Aggregate, value.Id, stored.AggregateVersion, proposed) };
            foreach (var party in new[] { proposed.Customer, proposed.Contractor ?? proposed.Customer }.Distinct())
            {
                var key = Prefix(party) + value.Id;
                var projection = await _database.GetNCAggregateSnapshotAsync(PartyAggregate, key);
                writes.Add(Write(PartyAggregate, key, projection?.AggregateVersion ?? 0, proposed));
            }
            var command = Guid.NewGuid();
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new(command, Aggregate + ".update",
                Aggregate, value.Id, writes, null, command, null, epoch, now, []));
            return result.Status == NCPersistentMutationStatus.Applied;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Permission matrix shared by all contract mutation entry points; completion never implies payment.</summary>
    public static bool CanChangeStatus(NCPlayerContractStatus status, int customer, int? contractor,
        int actor, NCPlayerContractAction action,
        out NCPlayerContractStatus next)
    {
        next = status;
        var active = next is NCPlayerContractStatus.Accepted or NCPlayerContractStatus.Ready;
        switch (action)
        {
            case NCPlayerContractAction.Accept when next == NCPlayerContractStatus.Open && actor != customer:
                next = NCPlayerContractStatus.Accepted; return true;
            case NCPlayerContractAction.Ready when next == NCPlayerContractStatus.Accepted && actor == contractor:
                next = NCPlayerContractStatus.Ready; return true;
            case NCPlayerContractAction.Complete when active && actor == customer:
                next = NCPlayerContractStatus.Completed; return true;
            case NCPlayerContractAction.Cancel when (active || next == NCPlayerContractStatus.Open) && actor == customer:
                next = NCPlayerContractStatus.Cancelled; return true;
            case NCPlayerContractAction.Withdraw when active && actor == contractor:
                next = NCPlayerContractStatus.Withdrawn; return true;
            default: return false;
        }
    }

    private static NCPlayerContractStatus EffectiveStatus(ContractRecord value, long now) =>
        value.Status is NCPlayerContractStatus.Open or NCPlayerContractStatus.Accepted or NCPlayerContractStatus.Ready &&
        value.Deadline is { } deadline && now >= deadline ? NCPlayerContractStatus.Expired : value.Status;

    private bool TryCharacter(EntityUid user, out NCCharacterId character)
    {
        character = default;
        return !Deleted(user) && _players.TryGetSessionByEntity(user, out var session) &&
            _preferences.TryGetSelectedNCCharacterId(session.UserId, out character);
    }

    private static bool ValidText(string text, int maximum) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= maximum && !text.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t');
    private static string Prefix(int character) => $"{character:D10}/";
    private static NCPersistentAggregateWrite Write<T>(string type, string id, long version, T value) =>
        new(Guid.NewGuid(), Aggregate + ".changed", type, id, version, 1,
            JsonSerializer.Serialize(value), "{}");

    private sealed record ContractRecord(string Id, long Version, int Customer, string CustomerName,
        int? Contractor, string ContractorName, string Title, string Terms, string Reward,
        NCPlayerContractStatus Status, long? Deadline);
}
