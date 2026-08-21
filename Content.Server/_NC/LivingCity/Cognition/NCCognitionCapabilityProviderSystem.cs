// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Projects existing actor, job, economy, social, crime, faction, medical and service authorities into facts. Reads
/// are bounded and keyed by one ActorId; no provider scans all entities or owns gameplay state.
/// </summary>
public sealed partial class NCCognitionCapabilityProviderSystem : EntitySystem
{
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;
    [Dependency] private NCEmploymentRegistrySystem _employment = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCMemoryRegistrySystem _memory = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    public async Task<NCCognitionFact[]> CollectAsync(
        NCCityActorRecord actor,
        NCCognitionPerceptionInput perception,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        int maximumFacts)
    {
        var actorId = actor.Snapshot.Identity.ActorId;
        var party = NCPartyRef.ForActor(actorId);
        var healthTask = _domains.GetHealthAsync(actorId);
        var inventoryTask = _domains.GetInventoryAsync(actorId);
        var employmentTask = _employment.FindCurrentByEmployeeAsync(party);
        var accountTask = _finance.GetPrimaryAccountAsync(party);
        var memoryTask = _memory.GetAsync(party);
        var membershipTask = _groups.GetMembershipAsync(actorId);
        await Task.WhenAll(healthTask, inventoryTask, employmentTask, accountTask, memoryTask, membershipTask);

        var sink = new NCCognitionFactSink(maximumFacts);
        var root = actor.Snapshot;
        var canAct = root.Lifecycle.Status is not (NCCityActorStatus.Dead or NCCityActorStatus.Archived or
            NCCityActorStatus.Unconscious) && !root.Lifecycle.Frozen;
        Add(sink, "actor.can-act", NCCognitionDomain.Core, "actor-root", actor.AggregateVersion,
            NCCognitionValue.FromBoolean(canAct), now, lifetime);
        Add(sink, "actor.incapacitated", NCCognitionDomain.Core, "actor-root", actor.AggregateVersion,
            NCCognitionValue.FromBoolean(root.Lifecycle.Status == NCCityActorStatus.Unconscious), now, lifetime);
        Add(sink, "crime.detained", NCCognitionDomain.Crime, "actor-root", actor.AggregateVersion,
            NCCognitionValue.FromBoolean(root.Lifecycle.Status is NCCityActorStatus.Arrested or
                NCCityActorStatus.Imprisoned), now, lifetime);
        Add(sink, "routine.due", NCCognitionDomain.Routine, "actor-root", actor.AggregateVersion,
            NCCognitionValue.FromBoolean(!string.IsNullOrWhiteSpace(root.Schedule.CurrentActivity)), now, lifetime);
        Add(sink, "routine.work-due", NCCognitionDomain.Routine, "actor-root", actor.AggregateVersion,
            NCCognitionValue.FromBoolean(root.Schedule.CurrentActivity.Equals("work", StringComparison.OrdinalIgnoreCase)),
            now, lifetime);
        Add(sink, "location.traveling", NCCognitionDomain.Routine, "actor-root", actor.AggregateVersion,
            NCCognitionValue.FromBoolean(root.Location.TravelPhase is NCCityActorTravelPhase.Planned or
                NCCityActorTravelPhase.InTransit), now, lifetime);

        var health = await healthTask;
        if (health != null)
        {
            var needsCare = health.Snapshot.Summary.OverallCondition != NCCityOverallCondition.Stable ||
                            health.Snapshot.Summary.Bleeding != NCCityBleedingState.None ||
                            health.Snapshot.Summary.Consciousness != NCCityConsciousnessState.Alert;
            Add(sink, "medical.needs-care", NCCognitionDomain.Medical, "actor-health", health.AggregateVersion,
                NCCognitionValue.FromBoolean(needsCare), now, lifetime);
            Add(sink, "medical.severity", NCCognitionDomain.Medical, "actor-health", health.AggregateVersion,
                NCCognitionValue.FromNumber(Severity(health.Snapshot.Summary.OverallCondition)), now, lifetime);
            Add(sink, "services.medical-needed", NCCognitionDomain.Services, "actor-health", health.AggregateVersion,
                NCCognitionValue.FromBoolean(needsCare), now, lifetime);
            Add(sink, "cyberware.degraded", NCCognitionDomain.Cyberware, "actor-health", health.AggregateVersion,
                NCCognitionValue.FromBoolean(health.Snapshot.Summary.Cyberware is
                    NCCityCyberwareState.Degraded or NCCityCyberwareState.Disabled),
                now, lifetime);
        }

        var inventory = await inventoryTask;
        if (inventory != null)
        {
            Add(sink, "inventory.unique-count", NCCognitionDomain.Economy, "actor-inventory",
                inventory.AggregateVersion,
                NCCognitionValue.FromNumber(inventory.Snapshot.Summary.UniqueItems.Length), now, lifetime);
        }

        var employment = await employmentTask;
        Add(sink, "employment.active", NCCognitionDomain.Jobs, "employment",
            employment?.AggregateVersion ?? 0,
            NCCognitionValue.FromBoolean(employment?.Snapshot.Status == NCEmploymentStatus.Active), now, lifetime);

        var account = await accountTask;
        if (account != null)
        {
            Add(sink, "economy.balance", NCCognitionDomain.Economy, "economy-account", account.AggregateVersion,
                NCCognitionValue.FromNumber(account.Snapshot.Balance), now, lifetime);
        }

        var memories = await memoryTask;
        if (memories != null)
        {
            Add(sink, "social.memory-count", NCCognitionDomain.Social, "memory-book", memories.AggregateVersion,
                NCCognitionValue.FromNumber(memories.Snapshot.Episodes.Count + memories.Snapshot.Summaries.Count),
                now, lifetime);
        }

        var membership = await membershipTask;
        if (membership != null && membership.Snapshot.GroupId.IsValid)
        {
            var group = await _groups.GetAsync(membership.Snapshot.GroupId);
            Add(sink, "faction.group-member", NCCognitionDomain.Faction, "group-membership",
                membership.AggregateVersion, NCCognitionValue.FromBoolean(true), now, lifetime);
            if (group != null)
            {
                Add(sink, "group.has-directive", NCCognitionDomain.Faction, "group", group.AggregateVersion,
                    NCCognitionValue.FromBoolean(!string.IsNullOrWhiteSpace(group.Snapshot.Directive.CurrentOrder)),
                    now, lifetime,
                    new NCCognitionSubjectRef(NCCognitionReferenceKind.Group, group.Snapshot.GroupId.ToString()));
            }
        }

        Add(sink, "perception.hostile-present", NCCognitionDomain.Safety, "local-perception", perception.Revision,
            NCCognitionValue.FromBoolean(perception.HostilePresent), now, lifetime);
        Add(sink, "perception.critical-person-present", NCCognitionDomain.Medical, "local-perception",
            perception.Revision, NCCognitionValue.FromBoolean(perception.CriticalPersonPresent), now, lifetime);
        Add(sink, "social.conversation-requested", NCCognitionDomain.Social, "physical-state", perception.Revision,
            NCCognitionValue.FromBoolean(perception.ConversationRequested), now, lifetime);

        return sink.ToArray();
    }

    private static void Add(
        NCCognitionFactSink sink,
        string key,
        NCCognitionDomain domain,
        string source,
        long version,
        NCCognitionValue value,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        NCCognitionSubjectRef subject = default)
    {
        sink.Add(new NCCognitionFact(key, subject, domain, source, Math.Max(0, version), value,
            now, now.Advance(lifetime)));
    }

    private static float Severity(NCCityOverallCondition condition) => condition switch
    {
        NCCityOverallCondition.Stable => 0f,
        NCCityOverallCondition.Compensating => 0.35f,
        NCCityOverallCondition.Critical => 0.75f,
        NCCityOverallCondition.ClinicalArrest => 0.95f,
        NCCityOverallCondition.Dead => 1f,
        _ => 0f,
    };
}
