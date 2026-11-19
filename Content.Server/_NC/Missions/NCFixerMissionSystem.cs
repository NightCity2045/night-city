// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Missions;
using Content.Shared._NC.Missions.Components;
using Content.Shared._NC.Missions.Prototypes;
using Content.Shared._NC.Missions.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Missions;

/// <summary>
/// Physical contacts issue durable work; organizers recruit consenting characters. The dungeon allocator remains
/// the authority for physical deployments. Player advertisements are an independent domain with no social rewards.
/// </summary>
public sealed partial class NCFixerMissionSystem : EntitySystem
{
    private const string Aggregate = "missions.fixer-work";
    private const string PartyAggregate = "missions.fixer-work-party";
    private const string OwnerAggregate = "missions.fixer-work-owner";
    private const string CommitmentsAggregate = "missions.fixer-work-commitments";
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NCMissionSystem _missions = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCReputationRegistrySystem _reputation = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Task> _pending = [];
    private readonly HashSet<(EntityUid Contact, EntityUid User)> _views = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCFixerContactAnchorComponent, MapInitEvent>(OnAnchorInit);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCFixerMissionComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCFixerMissionComponent, BoundUIClosedEvent>(OnClosed);
        SubscribeLocalEvent<NCFixerMissionComponent, ComponentShutdown>(OnContactShutdown);
        SubscribeLocalEvent<NCFixerMissionComponent, NCFixerMissionAcceptMessage>(OnAccept);
        SubscribeLocalEvent<NCFixerMissionComponent, NCFixerWorkActionMessage>(OnAction);
        SubscribeLocalEvent<NCFixerMissionComponent, NCFixerMissionRefreshMessage>(OnRefresh);
    }

    private void OnAnchorInit(Entity<NCFixerContactAnchorComponent> ent, ref MapInitEvent args)
    {
        if (!_prototypes.TryIndex(ent.Comp.Contact, out var contact))
            return;

        var position = _transform.GetMapCoordinates(ent);
        foreach (var body in _lookup.GetEntitiesInRange<NCFixerMissionComponent>(position, ent.Comp.SpawnRadius))
        {
            if (body.Comp.Contact == ent.Comp.Contact && IsContactBody(body, contact) && _mobState.IsAlive(body))
                return;
        }

        Spawn(contact.BodyPrototype, Transform(ent).Coordinates);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_pending.Count > 0) args.AddPendingWrite(Task.WhenAll(_pending.ToArray()));
        _views.Clear();
    }
    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        _attendance.Clear();
        _stagedRewards.Clear();
    }
    private void OnContactShutdown(Entity<NCFixerMissionComponent> ent, ref ComponentShutdown args) =>
        _views.RemoveWhere(value => value.Contact == ent.Owner);
    private async void Track(Task task)
    {
        _pending.Add(task);
        try { await task; }
        catch (Exception exception) { Log.Error($"Fixer mission operation failed: {exception}"); }
        finally { _pending.Remove(task); }
    }

    private static NCPartyRef ContactParty(NCFixerContactPrototype contact)
        => NCPersistentIdentity.Business($"mission-fixer/{contact.ID}");

    private async Task<NCAccountRecord?> EnsureContactBudgetAsync(NCFixerContactPrototype contact)
    {
        const string auditSource = "mission-fixer-bootstrap";
        var party = ContactParty(contact);
        var sourceKey = party.ToString();
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("fixer-mission", "register-contact", sourceKey),
            party,
            Loc.GetString(contact.Name),
            NCPersistentIdentity.Correlation("fixer-mission", "register-contact", sourceKey),
            AuditSource: auditSource));
        if (registration.Status is not (NCPartyDirectoryMutationStatus.Applied or NCPartyDirectoryMutationStatus.Duplicate) &&
            (await _directory.GetAsync(party, true))?.Snapshot.Lifecycle != NCPartyLifecycle.Active)
            return null;

        return (await _finance.EnsurePrimaryAccountWithOpeningBalanceAsync(party, contact.OpeningBudget, auditSource)).Account;
    }

    private void OnOpened(EntityUid uid, NCFixerMissionComponent component, BoundUIOpenedEvent args)
    {
        if (!NCFixerMissionUiKey.Key.Equals(args.UiKey)) return;
        var user = args.Actor;
        _views.Add((uid, user));
    }
    private void OnClosed(EntityUid uid, NCFixerMissionComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is { } user) _views.Remove((uid, user));
    }
    private void OnRefresh(EntityUid uid, NCFixerMissionComponent component, NCFixerMissionRefreshMessage args) =>
        Track(SendContactAsync(uid, args.Actor));
    private void OnAccept(EntityUid uid, NCFixerMissionComponent component, NCFixerMissionAcceptMessage args) =>
        Track(AcceptAndRefreshAsync(uid, args.Actor, args.Definition));
    private void OnAction(EntityUid uid, NCFixerMissionComponent component, NCFixerWorkActionMessage args) =>
        Track(ActAndRefreshAsync(uid, args.Actor, args));

    private async Task AcceptAndRefreshAsync(EntityUid uid, EntityUid user, string definition)
    {
        var success = await TryAcceptAsync(uid, user, definition);
        await SendContactAsync(uid, user, success ? "nc-fixer-work-accepted" : "nc-fixer-work-failed");
    }
    private async Task ActAndRefreshAsync(EntityUid uid, EntityUid user, NCFixerWorkActionMessage request)
    {
        var success = await TryActAsync(user, request, uid);
        await SendContactAsync(uid, user, success ? "nc-fixer-work-updated" : "nc-fixer-work-failed");
    }

    private bool CanContact(EntityUid uid, EntityUid user, out NCFixerContactPrototype contact)
    {
        contact = default!;
        if (!IsContactViewOpen(uid, user) || !TryComp<NCFixerMissionComponent>(uid, out var service) ||
            !_prototypes.TryIndex(service.Contact, out var found) || !IsContactBody(uid, found) ||
            !_mobState.IsAlive(uid) || !_mobState.IsAlive(user) || !_interaction.InRangeUnobstructed(user, uid))
            return false;

        contact = found;
        return true;
    }

    private bool IsContactBody(EntityUid uid, NCFixerContactPrototype contact)
        => MetaData(uid).EntityPrototype?.ID == contact.BodyPrototype.Id;

    private bool IsContactViewOpen(EntityUid uid, EntityUid user)
        => !Deleted(uid) && !Deleted(user) && _views.Contains((uid, user));

    public Task<bool> TryAcceptAsync(EntityUid uid, EntityUid user, string definitionId) =>
        TrackResult(TryAcceptCoreAsync(uid, user, definitionId));

    private async Task<bool> TryAcceptCoreAsync(EntityUid uid, EntityUid user, string definitionId)
    {
        if (!CanContact(uid, user, out var contact) || !TryCharacter(user, out var character) ||
            !contact.Missions.TryGetValue(new(definitionId), out var reward) || reward <= 0 ||
            !_prototypes.TryIndex<NCMissionDefinitionPrototype>(definitionId, out var definition)) return false;
        await _gate.WaitAsync();
        try
        {
            var trust = await GetTrustAsync(character, contact);
            if (!CanOffer(definition, contact, trust) || !CanContact(uid, user, out _) ||
                !_clock.TryGetCurrentTime(out var epoch, out var now))
                return false;

            await ReleaseExpiredCommitmentsLockedAsync(contact.ID);
            var budget = await EnsureContactBudgetAsync(contact);
            var commitments = await _database.GetNCAggregateSnapshotAsync(CommitmentsAggregate, contact.ID);
            var promised = commitments == null ? 0 :
                JsonSerializer.Deserialize<Dictionary<string, int>>(commitments.Payload)!.Values.Sum(value => (long) value);
            if (budget == null || budget.Snapshot.Balance - promised < reward ||
                !CanContact(uid, user, out _) || !TryCharacter(user, out var current) || current != character ||
                !_clock.TryGetCurrentTime(out var currentEpoch, out now) || currentEpoch != epoch)
                return false;
            var ownerKey = character.Value.ToString();
            var owner = await _database.GetNCAggregateSnapshotAsync(OwnerAggregate, ownerKey);
            if (owner != null)
            {
                var previous = JsonSerializer.Deserialize<WorkRecord>(owner.Payload);
                if (previous != null) previous = await RefreshOutcomeLockedAsync(previous);
                if (previous != null && (previous.State is NCFixerWorkState.Planning or NCFixerWorkState.Deploying or NCFixerWorkState.Active ||
                    now.Ticks - previous.CreatedAt < contact.OfferCooldown.Ticks)) return false;
            }
            var id = Guid.NewGuid().ToString("D");
            var work = new WorkRecord(id, 1, definitionId, contact.ID, character.Value, [character.Value], [],
                NCFixerWorkState.Planning, null, epoch.Value, now.Ticks, now.Advance(contact.DeliveryWindow).Ticks,
                reward, false, false, false, null);
            return await SaveAsync(work, 0);
        }
        finally { _gate.Release(); }
    }

    public Task<NCFixerMissionUpdateMessage> GetAgentViewAsync(EntityUid user, string notice = "") =>
        TrackResult(GetAgentViewCoreAsync(user, notice));

    private async Task<NCFixerMissionUpdateMessage> GetAgentViewCoreAsync(EntityUid user, string notice)
    {
        if (!TryCharacter(user, out var character)) return new([], [], 0, false, "nc-fixer-work-failed");
        var rows = await _database.GetNCAggregateSnapshotPrefixPageDescendingAsync(PartyAggregate,
            Prefix(character.Value), null, 50);
        var work = new List<NCFixerWorkEntry>();
        var names = new Dictionary<int, string>();
        foreach (var row in rows)
        {
            var value = JsonSerializer.Deserialize<WorkRecord>(row.Payload);
            if (value != null)
            {
                await _gate.WaitAsync();
                try
                {
                    var latest = await LoadAsync(value.Id);
                    value = latest == null ? null : await RefreshOutcomeLockedAsync(latest);
                }
                finally { _gate.Release(); }
            }
            if (value == null || (value.Organizer != character.Value && !value.Crew.Contains(character.Value) &&
                !value.Invited.Contains(character.Value))) continue;
            var status = value.State;
            int? remaining = null;
            if (value.RunId is { } runId && _missions.TryGetRun(new(runId), out var run))
            {
                remaining = Math.Max(0, (int) (run.ExpiresAt - DateTime.UtcNow).TotalSeconds);
                if (run.State == NCMissionRunState.Failed) status = NCFixerWorkState.Failed;
            }
            foreach (var member in value.Crew.Append(value.Organizer).Distinct())
                if (!names.ContainsKey(member))
                    names[member] = (await _parties.ResolveAsync(NCPartyRef.ForCharacter(new(member)))).DisplayName;
            var deliverySeconds = _clock.TryGetCurrentTime(out _, out var now)
                ? (int) Math.Clamp((value.Deadline - now.Ticks) / TimeSpan.TicksPerSecond, 0, int.MaxValue) : 0;
            EntityUid? entrance = null;
            var busy = false;
            if ((status is NCFixerWorkState.Planning or NCFixerWorkState.Deploying or NCFixerWorkState.Active) &&
                _prototypes.TryIndex<NCMissionDefinitionPrototype>(value.Definition, out var mission) &&
                _prototypes.TryIndex(mission.Template, out var layout))
            {
                var slot = new NCMissionSlotId(layout.TargetZoneId);
                entrance = _missions.FindEntrance(slot, user);
                busy = status == NCFixerWorkState.Planning && _missions.IsSiteBusy(slot);
            }
            work.Add(new(value.Id, value.Version, value.Definition,
                _prototypes.TryIndex<NCFixerContactPrototype>(value.Contact, out var contact) ? Loc.GetString(contact.Name) : value.Contact,
                value.Organizer, value.Crew, value.Invited, status, value.Reward, value.Paid, remaining,
                deliverySeconds, value.Surrendered, names[value.Organizer], value.Crew.Select(id => names[id]).ToList())
                { Location = MissionLocation(value.Definition), Busy = busy, Entrance = entrance is { } door && _coordinates.TryGetCoordinates(door, out var point) ? point : null });
        }
        var reputation = await _reputation.GetAsync(NCPartyRef.ForCharacter(character), true);
        var scores = new List<NCFixerReputationEntry>();
        // Only expose this character's mission domains and trust in configured contacts.
        foreach (var contact in _prototypes.EnumeratePrototypes<NCFixerContactPrototype>())
        {
            var entries = reputation?.Snapshot.Entries;
            var trustKey = _prototypes.Index(contact.TrustDomain).StableKey;
            var trust = entries?.FirstOrDefault(entry => entry.Audience.Channel == NCReputationChannel.Professional &&
                entry.Audience.DomainKey == trustKey)?.Score ?? 0;
            var executorKey = _prototypes.Index(contact.ExecutorDomain).StableKey;
            var organizerKey = _prototypes.Index(contact.OrganizerDomain).StableKey;
            var executor = entries?.FirstOrDefault(entry => entry.Audience.Channel == NCReputationChannel.Professional &&
                entry.Audience.DomainKey == executorKey)?.Score ?? 0;
            var organizer = entries?.FirstOrDefault(entry => entry.Audience.Channel == NCReputationChannel.Professional &&
                entry.Audience.DomainKey == organizerKey)?.Score ?? 0;
            var available = contact.Missions.Keys.Count(id => _prototypes.TryIndex(id, out var definition) &&
                CanOffer(definition, contact, trust));
            scores.Add(new(Loc.GetString(contact.Name), executor, organizer, trust,
                contact.OrganizerTrustThreshold, available, contact.Missions.Count)
                { Position = FindContactPosition(contact, user) });
        }
        return new([], work, character.Value, false, notice) { Reputation = scores };
    }

    private NCMapCoordinates? FindContactPosition(NCFixerContactPrototype contact, EntityUid user)
    {
        if (!_coordinates.TryGetCoordinates(user, out var here))
            return null;

        NCMapCoordinates? result = null;
        var nearest = float.PositiveInfinity;
        var bodies = EntityQueryEnumerator<NCFixerMissionComponent>();
        while (bodies.MoveNext(out var uid, out var service))
        {
            if (service.Contact.Id != contact.ID || !IsContactBody(uid, contact) || !_mobState.IsAlive(uid) ||
                !_coordinates.TryGetCoordinates(uid, out var point) || point.NetworkId != here.NetworkId)
                continue;

            var distance = Vector2.DistanceSquared(here.Position, point.Position) +
                (point.Z == here.Z ? 0f : 1000000f);
            if (distance >= nearest)
                continue;

            nearest = distance;
            result = point;
        }

        return result;
    }

    private async Task SendContactAsync(EntityUid uid, EntityUid user, string notice = "")
    {
        if (!IsContactViewOpen(uid, user))
            return;

        if (!CanContact(uid, user, out var contact) || !TryCharacter(user, out var character))
        {
            SendContactNotice(uid, user, "nc-fixer-contact-unavailable");
            return;
        }

        try
        {
            var data = await GetAgentViewAsync(user, notice);
            var trust = await GetTrustAsync(character, contact);
            if (!IsContactViewOpen(uid, user))
                return;
            if (!CanContact(uid, user, out _) || !TryCharacter(user, out var current) || current != character)
            {
                SendContactNotice(uid, user, "nc-fixer-contact-unavailable");
                return;
            }

            var offers = new List<NCFixerMissionOffer>();
            foreach (var (id, reward) in contact.Missions)
            {
                if (_prototypes.TryIndex(id, out var definition))
                    offers.Add(new(id.Id, reward, CanOffer(definition, contact, trust))
                        { Location = MissionLocation(id.Id) });
            }

            _ui.ServerSendUiMessage(uid, NCFixerMissionUiKey.Key,
                new NCFixerMissionUpdateMessage(offers, data.Work, character.Value, true, notice,
                    trust >= contact.OrganizerTrustThreshold ? "nc-fixer-trust-organizer" : "nc-fixer-trust-new")
                    { Reputation = data.Reputation }, user);
        }
        catch
        {
            if (IsContactViewOpen(uid, user))
                SendContactNotice(uid, user, "nc-fixer-contact-load-failed");
            throw;
        }
    }

    private void SendContactNotice(EntityUid uid, EntityUid user, string notice)
        => _ui.ServerSendUiMessage(uid, NCFixerMissionUiKey.Key,
            new NCFixerMissionUpdateMessage([], [], 0, false, notice), user);

    private async Task<int> GetTrustAsync(NCCharacterId character, NCFixerContactPrototype contact)
    {
        var reputation = await _reputation.GetAsync(NCPartyRef.ForCharacter(character), true);
        var trustKey = _prototypes.Index(contact.TrustDomain).StableKey;
        return reputation?.Snapshot.Entries.FirstOrDefault(value =>
            value.Audience.Channel == NCReputationChannel.Professional && value.Audience.DomainKey == trustKey)?.Score ?? 0;
    }

    private string MissionLocation(string id) =>
        _prototypes.TryIndex<NCMissionDefinitionPrototype>(id, out var definition) &&
        _prototypes.TryIndex(definition.Template, out var template) &&
        _zones.TryGetMissionSlot(new(template.TargetZoneId), out var zone) ? zone.Name : string.Empty;

    private async Task<T> TrackResult<T>(Task<T> task)
    {
        _pending.Add(task);
        try { return await task; }
        finally { _pending.Remove(task); }
    }

    private async Task<WorkRecord> RefreshOutcomeLockedAsync(WorkRecord value)
    {
        if (value.Surrendered || value.State is NCFixerWorkState.Completed or NCFixerWorkState.Cancelled or NCFixerWorkState.Failed ||
            !_clock.TryGetCurrentTime(out var epoch, out var now)) return value;
        // Physical targets do not survive a round boundary. Expiry closes work without fabricating blame or payment.
        var failed = value.Epoch != epoch.Value || now.Ticks >= value.Deadline ||
            (value.RunId is { } runId && _missions.TryGetRun(new(runId), out var run) && run.State == NCMissionRunState.Failed);
        if (!failed) return value;
        var expired = value with { Version = value.Version + 1, State = NCFixerWorkState.Failed };
        return await SaveAsync(expired, value.Version) ? expired : value;
    }

    private async Task ReleaseExpiredCommitmentsLockedAsync(string contact)
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(CommitmentsAggregate, contact);
        if (stored == null) return;
        var commitments = JsonSerializer.Deserialize<Dictionary<string, int>>(stored.Payload);
        if (commitments == null) return;
        // Only the contact's outstanding funded promises are examined when new work is requested.
        foreach (var id in commitments.Keys)
        {
            var work = await LoadAsync(id);
            if (work != null) await RefreshOutcomeLockedAsync(work);
        }
    }
    private bool CanOffer(NCMissionDefinitionPrototype definition, NCFixerContactPrototype contact, int trust) =>
        contact.OrganizerTrustThreshold > 0 && contact.PresenceCheckInterval > TimeSpan.Zero &&
        contact.DeliveryWindow > TimeSpan.Zero && definition.DeliveryObjective != null &&
        definition.Difficulty is { } difficulty && _prototypes.TryIndex(difficulty, out var tier) &&
        (tier.Order == 1 || trust >= contact.OrganizerTrustThreshold * (tier.Order - 1));

    private bool TryCharacter(EntityUid user, out NCCharacterId character)
    {
        character = default;
        return !Deleted(user) && _players.TryGetSessionByEntity(user, out var session) &&
            _preferences.TryGetSelectedNCCharacterId(session.UserId, out character);
    }
    private async Task<WorkRecord?> LoadAsync(string id)
    {
        if (!Guid.TryParse(id, out _)) return null;
        var stored = await _database.GetNCAggregateSnapshotAsync(Aggregate, id);
        return stored == null ? null : JsonSerializer.Deserialize<WorkRecord>(stored.Payload);
    }

    private async Task<bool> SaveAsync(WorkRecord value, long expected)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now)) return false;
        var writes = new List<NCPersistentAggregateWrite> { Write(Aggregate, value.Id, expected, value) };
        var commitments = await _database.GetNCAggregateSnapshotAsync(CommitmentsAggregate, value.Contact);
        var reserved = commitments == null ? new Dictionary<string, int>() :
            JsonSerializer.Deserialize<Dictionary<string, int>>(commitments.Payload)!;
        var wasReserved = reserved.ContainsKey(value.Id);
        var shouldReserve = !value.Paid && value.State is not (NCFixerWorkState.Completed or NCFixerWorkState.Cancelled or NCFixerWorkState.Failed);
        if (wasReserved != shouldReserve)
        {
            if (shouldReserve) reserved[value.Id] = value.Reward;
            else reserved.Remove(value.Id);
            writes.Add(new(Guid.NewGuid(), Aggregate + ".commitments-changed", CommitmentsAggregate, value.Contact,
                commitments?.AggregateVersion ?? 0, 1, JsonSerializer.Serialize(reserved), "{}"));
        }
        var ownerKey = value.Organizer.ToString();
        var owner = await _database.GetNCAggregateSnapshotAsync(OwnerAggregate, ownerKey);
        writes.Add(Write(OwnerAggregate, ownerKey, owner?.AggregateVersion ?? 0, value));
        var previous = expected > 0 ? await LoadAsync(value.Id) : null;
        foreach (var party in value.Crew.Concat(value.Invited).Append(value.Organizer)
                     .Concat(previous?.Crew ?? []).Concat(previous?.Invited ?? []).Distinct())
        {
            var key = Prefix(party) + $"{value.CreatedAt:D19}/" + value.Id;
            var projection = await _database.GetNCAggregateSnapshotAsync(PartyAggregate, key);
            writes.Add(Write(PartyAggregate, key, projection?.AggregateVersion ?? 0, value));
        }
        var command = Guid.NewGuid();
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(command, Aggregate + ".update",
            Aggregate, value.Id, writes, null, command, null, epoch, now, []));
        return result.Status == NCPersistentMutationStatus.Applied;
    }
    private static string Prefix(int character) => $"{character:D10}/";
    private static NCPersistentAggregateWrite Write(string type, string id, long version, WorkRecord value) =>
        new(Guid.NewGuid(), Aggregate + ".changed", type, id, version, 1, JsonSerializer.Serialize(value), "{}");
    private sealed record WorkRecord(string Id, long Version, string Definition, string Contact, int Organizer,
        List<int> Crew, List<int> Invited, NCFixerWorkState State, Guid? RunId, Guid Epoch, long CreatedAt,
        long Deadline, int Reward, bool Surrendered, bool Paid, bool SocialApplied, Guid? PaymentId,
        List<int>? Attended = null);
}
