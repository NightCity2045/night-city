// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Bank.Cash;
using Content.Server._NC.LivingCity.Economy.Cash;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Missions;
using Content.Shared._NC.Missions.Components;
using Content.Shared._NC.Missions.Prototypes;
using Content.Shared._NC.Missions.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Containers;

namespace Content.Server._NC.Missions;

public sealed partial class NCFixerMissionSystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private NCCashHandoffSystem _cash = default!;
    [Dependency] private NCCashTerminalEscrowSystem _cashPhysical = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    private readonly Dictionary<Guid, NCCashEscrowCapture> _stagedRewards = [];

    public Task<bool> TryActAsync(EntityUid user, NCFixerWorkActionMessage request, EntityUid? contactEntity = null) =>
        TrackResult(TryActCoreAsync(user, request, contactEntity));

    private async Task<bool> TryActCoreAsync(EntityUid user, NCFixerWorkActionMessage request, EntityUid? contactEntity)
    {
        if (!TryCharacter(user, out var character)) return false;
        await _gate.WaitAsync();
        try
        {
            var work = await LoadAsync(request.Id);
            if (work == null || work.Version != request.Version ||
                !TryCharacter(user, out var current) || character != current ||
                !_prototypes.TryIndex<NCMissionDefinitionPrototype>(work.Definition, out var definition) ||
                !_prototypes.TryIndex<NCFixerContactPrototype>(work.Contact, out var contact) ||
                !_clock.TryGetCurrentTime(out var epoch, out var now)) return false;
            var organizer = work.Organizer == character.Value;
            if (!work.Surrendered && (work.Epoch != epoch.Value || now.Ticks >= work.Deadline) &&
                request.Action != NCFixerWorkAction.Cancel) return false;
            var planning = work.State == NCFixerWorkState.Planning;
            var proposed = work with { Version = work.Version + 1 };
            switch (request.Action)
            {
                case NCFixerWorkAction.Invite when organizer && planning:
                    if (request.Target <= 0 || work.Crew.Contains(request.Target) || work.Invited.Contains(request.Target) ||
                        work.Crew.Count + work.Invited.Count >= definition.MaximumParticipants) return false;
                    // Resolve the persistent character; arbitrary client-supplied numeric IDs cannot create participants.
                    if (!(await _parties.ResolveAsync(NCPartyRef.ForCharacter(new(request.Target)), true)).IsResolved) return false;
                    proposed = proposed with { Invited = [.. work.Invited, request.Target] };
                    break;
                case NCFixerWorkAction.Join when planning && work.Invited.Contains(character.Value):
                    if (work.Crew.Count >= definition.MaximumParticipants) return false;
                    proposed = proposed with { Crew = [.. work.Crew, character.Value], Invited = work.Invited.Where(id => id != character.Value).ToList() };
                    break;
                case NCFixerWorkAction.Decline when planning && !organizer && (work.Invited.Contains(character.Value) || work.Crew.Contains(character.Value)):
                    proposed = proposed with { Crew = work.Crew.Where(id => id != character.Value).ToList(), Invited = work.Invited.Where(id => id != character.Value).ToList() };
                    break;
                case NCFixerWorkAction.Cancel when organizer && work.State is not (NCFixerWorkState.Completed or NCFixerWorkState.Cancelled):
                    if (work.Surrendered) return false;
                    proposed = proposed with { State = NCFixerWorkState.Cancelled };
                    if (!await SaveAsync(proposed, work.Version)) return false;
                    if (work.RunId is { } cancelRun) _missions.TryFinishRun(new(cancelRun), NCMissionRunResult.Aborted, out _);
                    return true;
                case NCFixerWorkAction.Launch when organizer && work.State is NCFixerWorkState.Planning or NCFixerWorkState.Deploying:
                    if (work.Epoch != epoch.Value || now.Ticks >= work.Deadline || definition.DeliveryObjective == null) return false;
                    // Save the run identity before requesting deployment. A repeated launch joins that exact run.
                    proposed = proposed with { State = NCFixerWorkState.Deploying, RunId = Guid.Parse(work.Id) };
                    if (!await SaveAsync(proposed, work.Version)) return false;
                    var result = await _missions.TryStartRunAsync(new(work.Definition),
                        work.Crew.Select(id => new NCCharacterId(id)).ToArray(), requestedRunId: new(proposed.RunId.Value));
                    if (result.Success && !_attendance.ContainsKey(result.RunId.Value))
                        _attendance[result.RunId.Value] = new(work.Crew, contact.PresenceCheckInterval, work.Deadline);
                    var deployed = proposed with { Version = proposed.Version + 1,
                        State = result.Success ? NCFixerWorkState.Active : NCFixerWorkState.Planning,
                        RunId = result.Success ? result.RunId.Value : null };
                    return await SaveAsync(deployed, proposed.Version) && result.Success;
                case NCFixerWorkAction.Deliver when organizer && work.State is NCFixerWorkState.Active or NCFixerWorkState.Completed:
                    if (contactEntity is not { } physical || !CanContact(physical, user, out var issuer) || issuer.ID != work.Contact)
                        return false;
                    return await TryDeliverAsync(physical, user, work, contact);
                default: return false;
            }
            return await SaveAsync(proposed, work.Version);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> TryDeliverAsync(EntityUid fixer, EntityUid user, WorkRecord work, NCFixerContactPrototype contact)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) ||
            (!work.Surrendered && (work.Epoch != epoch.Value || now.Ticks >= work.Deadline))) return false;
        if (!work.Surrendered)
        {
            if (!_hands.TryGetActiveItem(user, out var item) || !HasComp<NCFixerMissionObjectiveComponent>(item) ||
                !TryComp<NCMissionRunEntityComponent>(item, out var provenance) || provenance.RunId != work.RunId)
                return false;
            // Retain the actual target while persistence is pending; no second handoff can claim it concurrently.
            var container = _containers.EnsureContainer<Container>(fixer, "nc_mission_delivery");
            if (!_containers.Insert(item.Value, container)) return false;
            var surrendered = work with { Version = work.Version + 1, Surrendered = true,
                Attended = work.RunId is { } attendedRun && _attendance.TryGetValue(attendedRun, out var attendance)
                    ? attendance.Present.ToList() : [] };
            if (!await SaveAsync(surrendered, work.Version))
            {
                _containers.Remove(item.Value, container, destination: Transform(fixer).Coordinates);
                return false;
            }
            work = surrendered;
            QueueDel(item.Value);
            if (work.RunId is { } runId) _missions.TryFinishRun(new(runId), NCMissionRunResult.Success, out _);
        }
        if (!work.Paid)
        {
            var paid = await TryPayAsync(fixer, user, work, contact);
            if (paid == null) return false;
            work = paid;
        }
        if (!work.SocialApplied)
        {
            if (!await ApplySocialAsync(work, contact)) return false;
            var completed = work with { Version = work.Version + 1, State = NCFixerWorkState.Completed, SocialApplied = true };
            if (!await SaveAsync(completed, work.Version)) return false;
        }
        return true;
    }

    private async Task<WorkRecord?> TryPayAsync(EntityUid fixer, EntityUid user, WorkRecord work, NCFixerContactPrototype contact)
    {
        var payer = NCPartyRef.ForActor(new(contact.ActorId));
        var payee = NCPartyRef.ForCharacter(new(work.Organizer));
        if (work.PaymentId == null)
        {
            var staged = work with { Version = work.Version + 1, PaymentId = Guid.NewGuid() };
            if (!await SaveAsync(staged, work.Version)) return null;
            work = staged;
        }
        var handoffId = new NCCashHandoffId(work.PaymentId!.Value);
        var result = await _cash.StartAsync(new(
            NCPersistentIdentity.Command("fixer-mission", "pay", handoffId.ToString()), handoffId,
            NCPersistentIdentity.Transaction("fixer-mission", "pay", handoffId.ToString()), payer,
            NCCashHandoffDirection.Withdrawal, -work.Reward, work.Reward, 0, payee, work.Id,
            Guid.Parse(work.Id), AuditSource: "fixer-mission-reward"));
        if (result.Status is not (NCCashHandoffMutationStatus.Applied or NCCashHandoffMutationStatus.Duplicate))
        {
            if (result.Status is NCCashHandoffMutationStatus.InsufficientFunds or NCCashHandoffMutationStatus.Rejected)
                await SaveAsync(work with { Version = work.Version + 1, PaymentId = null }, work.Version);
            return null;
        }
        if (result.Handoff?.Snapshot.Status == NCCashHandoffStatus.Reversed)
        {
            await SaveAsync(work with { Version = work.Version + 1, PaymentId = null }, work.Version);
            return null;
        }
        if (result.Handoff?.Snapshot.Status != NCCashHandoffStatus.Completed)
        {
            if (!CanContact(fixer, user, out _)) return null;
            if (!_stagedRewards.TryGetValue(handoffId.Value, out var physical))
            {
                physical = _cashPhysical.StageWithdrawal(fixer, _finance.Configuration.CurrencyStack, work.Reward);
                if (physical == null)
                {
                    await _cash.ReverseAsync(handoffId, payee, "fixer-mission-cash-unavailable");
                    return null;
                }
                _stagedRewards[handoffId.Value] = physical;
            }
            var completed = await _cash.CompleteAsync(handoffId, payee, "fixer-mission-reward");
            if (completed.Status is not (NCCashHandoffMutationStatus.Applied or NCCashHandoffMutationStatus.Duplicate))
                return null;
        }
        // Uncertain completion retains the same cash in its private container for a subsequent retry.
        if (_stagedRewards.TryGetValue(handoffId.Value, out var stagedCash))
        {
            var physical = stagedCash;
            if (!_cashPhysical.ReleaseWithdrawal(fixer, physical))
            {
                _cashPhysical.Consume(physical);
                _stagedRewards.Remove(handoffId.Value);
                Log.Error($"Mission {work.Id}: completed reward could not leave the physical fixer cash container.");
                return null;
            }
            _stagedRewards.Remove(handoffId.Value);
        }
        var paid = work with { Version = work.Version + 1, Paid = true };
        return await SaveAsync(paid, work.Version) ? paid : null;
    }

    private async Task<bool> ApplySocialAsync(WorkRecord work, NCFixerContactPrototype contact)
    {
        var issuer = NCPartyRef.ForActor(new(contact.ActorId));
        var present = work.Attended ?? [];
        var organized = present.Any(member => member != work.Organizer);
        foreach (var member in present.Concat(organized ? [work.Organizer] : Array.Empty<int>()).Distinct())
        {
            var subject = NCPartyRef.ForCharacter(new(member));
            var organizing = member == work.Organizer && organized;
            var stable = $"{work.Id}/{member}";
            var domain = organizing ? contact.OrganizerDomain : contact.ExecutorDomain;
            var professional = await _reputation.ApplyEffectAsync(new(
                NCPersistentIdentity.Command("fixer-mission", "professional", stable), subject,
                new(NCReputationChannel.Professional, default, domain), contact.SuccessEffect, Guid.Parse(work.Id),
                1, issuer, Guid.Parse(work.Id), AuditSource: "verified-mission-delivery"));
            if (professional.Status is not (NCReputationMutationStatus.Applied or NCReputationMutationStatus.Duplicate)) return false;
            // Contact access comes from actually organizing a consenting team, not farming solo kills or advertisements.
            if (!organizing) continue;
            var trust = await _reputation.ApplyEffectAsync(new(
                NCPersistentIdentity.Command("fixer-mission", "contact-trust", stable), subject,
                new(NCReputationChannel.Personal, issuer), contact.SuccessEffect, Guid.Parse(work.Id),
                1, issuer, Guid.Parse(work.Id), AuditSource: "verified-mission-organization"));
            if (trust.Status is not (NCReputationMutationStatus.Applied or NCReputationMutationStatus.Duplicate)) return false;
        }
        return true;
    }
}
