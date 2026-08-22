// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Social;
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
    [Dependency] private NCSocialProfileRegistrySystem _socialProfiles = default!;
    [Dependency] private NCRelationshipRegistrySystem _relationships = default!;
    [Dependency] private NCRumorRegistrySystem _rumors = default!;
    [Dependency] private NCSocialObligationRegistrySystem _obligations = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;

    private readonly ConcurrentDictionary<NCActorId, MissingCapabilityEntry> _missingCapabilities = [];
    private long _suppressedCapabilityReads;

    public int MissingCapabilityCacheActors => _missingCapabilities.Count;
    public long SuppressedCapabilityReads => Interlocked.Read(ref _suppressedCapabilityReads);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => ResetMissingCache());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ResetMissingCache());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => ResetMissingCache());
    }

    public async Task<NCCognitionFact[]> CollectAsync(
        NCCityActorRecord actor,
        NCCognitionPerceptionInput perception,
        NCActiveWorldTime now,
        TimeSpan lifetime,
        int maximumFacts,
        TimeSpan missingCapabilityRefreshInterval)
    {
        var actorId = actor.Snapshot.Identity.ActorId;
        var party = NCPartyRef.ForActor(actorId);
        var cacheValid = _missingCapabilities.TryGetValue(actorId, out var missingEntry) &&
                         missingEntry.ExpiresAt > now;
        var missing = cacheValid ? missingEntry.Flags : MissingCapability.None;
        if (cacheValid)
            Interlocked.Add(ref _suppressedCapabilityReads, CountFlags(missing));
        var healthTask = _domains.GetHealthAsync(actorId);
        var inventoryTask = _domains.GetInventoryAsync(actorId);
        var employmentTask = LoadUnlessMissing(missing, MissingCapability.Employment,
            () => _employment.FindCurrentByEmployeeAsync(party));
        var accountTask = LoadUnlessMissing(missing, MissingCapability.Account,
            () => _finance.GetPrimaryAccountAsync(party));
        var memoryTask = LoadUnlessMissing(missing, MissingCapability.Memory,
            () => _memory.GetAsync(party));
        var socialProfileTask = LoadUnlessMissing(missing, MissingCapability.SocialProfile,
            () => _socialProfiles.GetAsync(actorId));
        var socialLimit = Math.Min(32, Math.Max(1, maximumFacts / 8));
        var relationshipTask = LoadUnlessMissing(missing, MissingCapability.Relationships,
            () => _relationships.GetForObserverAsync(party, socialLimit));
        var rumorTask = LoadUnlessMissing(missing, MissingCapability.Rumors,
            () => _rumors.GetKnownAsync(party, NCMemoryPrivacy.Secret, socialLimit));
        var obligationTask = LoadUnlessMissing(missing, MissingCapability.Obligations,
            () => _obligations.GetForPartyAsync(party, socialLimit));
        var membershipTask = LoadUnlessMissing(missing, MissingCapability.GroupMembership,
            () => _groups.GetMembershipAsync(actorId));
        await Task.WhenAll(healthTask, inventoryTask, employmentTask, accountTask, memoryTask, socialProfileTask,
            relationshipTask, rumorTask, obligationTask, membershipTask);

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

        var socialProfile = await socialProfileTask;
        if (socialProfile != null)
        {
            var profile = socialProfile.Snapshot;
            Add(sink, "personality.risk-tolerance", NCCognitionDomain.Social, "social-profile",
                socialProfile.AggregateVersion, NCCognitionValue.FromNumber(profile.Personality.RiskTolerance / 1000f),
                now, lifetime);
            Add(sink, "personality.aggression", NCCognitionDomain.Social, "social-profile",
                socialProfile.AggregateVersion, NCCognitionValue.FromNumber(profile.Personality.Aggression / 1000f),
                now, lifetime);
            Add(sink, "personality.empathy", NCCognitionDomain.Social, "social-profile",
                socialProfile.AggregateVersion, NCCognitionValue.FromNumber(profile.Personality.Empathy / 1000f),
                now, lifetime);
            Add(sink, "personality.secrecy", NCCognitionDomain.Social, "social-profile",
                socialProfile.AggregateVersion, NCCognitionValue.FromNumber(profile.Personality.Secrecy / 1000f),
                now, lifetime);
            Add(sink, "values.survival", NCCognitionDomain.Social, "social-profile",
                socialProfile.AggregateVersion, NCCognitionValue.FromNumber(profile.Values.Survival / 1000f),
                now, lifetime);
            foreach (var (capability, modifier) in profile.CapabilityUtilityModifiers)
            {
                Add(sink, $"social.utility-modifier.{capability}", NCCognitionDomain.Social, "social-profile",
                    socialProfile.AggregateVersion, NCCognitionValue.FromNumber(modifier), now, lifetime);
            }
        }

        var relationships = await relationshipTask ?? [];
        foreach (var relationship in relationships)
        {
            var value = relationship.Snapshot;
            var subject = new NCCognitionSubjectRef(NCCognitionReferenceKind.Party, value.Subject.ToString());
            var source = $"relationship:{value.RelationshipId}";
            Add(sink, "social.relationship.trust", NCCognitionDomain.Social, source,
                relationship.AggregateVersion, NCCognitionValue.FromNumber(value.Trust / 1000f), now, lifetime,
                subject);
            Add(sink, "social.relationship.hostility", NCCognitionDomain.Social, source,
                relationship.AggregateVersion, NCCognitionValue.FromNumber(value.Hostility / 1000f), now, lifetime,
                subject);
            Add(sink, "social.relationship.debt", NCCognitionDomain.Social, source,
                relationship.AggregateVersion, NCCognitionValue.FromNumber(value.Debt), now, lifetime, subject);
            Add(sink, "social.relationship.obligation", NCCognitionDomain.Social, source,
                relationship.AggregateVersion, NCCognitionValue.FromNumber(value.Obligation / 1000f), now, lifetime,
                subject);
        }

        var rumors = await rumorTask ?? [];
        foreach (var rumor in rumors)
        {
            var subject = new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, rumor.Subject.StableId);
            Add(sink, $"social.rumor.{rumor.TopicKey}", NCCognitionDomain.Social,
                $"rumor:{rumor.KnowledgeId}", rumor.DataVersion,
                NCCognitionValue.FromNumber(rumor.Confidence / 1000f), now, lifetime, subject);
        }

        var obligations = await obligationTask ?? [];
        Add(sink, "social.obligation.active-count", NCCognitionDomain.Social, "social-obligation-index",
            obligations.Count == 0 ? 0 : obligations.Max(value => value.AggregateVersion),
            NCCognitionValue.FromNumber(obligations.Count), now, lifetime);
        foreach (var obligation in obligations)
        {
            var value = obligation.Snapshot;
            var counterparty = value.Debtor == party ? value.Creditor : value.Debtor;
            Add(sink, value.Debtor == party ? "social.obligation.owed" : "social.obligation.expected",
                NCCognitionDomain.Social, $"social-obligation:{value.ObligationId}", obligation.AggregateVersion,
                NCCognitionValue.FromBoolean(true), now, lifetime,
                new NCCognitionSubjectRef(NCCognitionReferenceKind.Party, counterparty.ToString()));
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

        var refreshedMissing = MissingCapability.None;
        if (employment == null)
            refreshedMissing |= MissingCapability.Employment;
        if (account == null)
            refreshedMissing |= MissingCapability.Account;
        if (memories == null)
            refreshedMissing |= MissingCapability.Memory;
        if (socialProfile == null)
            refreshedMissing |= MissingCapability.SocialProfile;
        if (relationships.Count == 0)
            refreshedMissing |= MissingCapability.Relationships;
        if (rumors.Count == 0)
            refreshedMissing |= MissingCapability.Rumors;
        if (obligations.Count == 0)
            refreshedMissing |= MissingCapability.Obligations;
        if (membership == null)
            refreshedMissing |= MissingCapability.GroupMembership;
        if (refreshedMissing == MissingCapability.None || missingCapabilityRefreshInterval <= TimeSpan.Zero)
        {
            _missingCapabilities.TryRemove(actorId, out _);
        }
        else
        {
            var expiresAt = cacheValid
                ? missingEntry.ExpiresAt
                : now.Advance(missingCapabilityRefreshInterval);
            _missingCapabilities[actorId] = new MissingCapabilityEntry(refreshedMissing, expiresAt);
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

    private static Task<T> LoadUnlessMissing<T>(
        MissingCapability missing,
        MissingCapability capability,
        Func<Task<T>> load)
    {
        return (missing & capability) != 0 ? Task.FromResult(default(T)!) : load();
    }

    private void ResetMissingCache()
    {
        _missingCapabilities.Clear();
        Interlocked.Exchange(ref _suppressedCapabilityReads, 0);
    }

    private static int CountFlags(MissingCapability flags)
    {
        var value = (ushort) flags;
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }

    [Flags]
    private enum MissingCapability : ushort
    {
        None = 0,
        Employment = 1 << 0,
        Account = 1 << 1,
        Memory = 1 << 2,
        SocialProfile = 1 << 3,
        Relationships = 1 << 4,
        Rumors = 1 << 5,
        Obligations = 1 << 6,
        GroupMembership = 1 << 7,
    }

    private readonly record struct MissingCapabilityEntry(
        MissingCapability Flags,
        NCActiveWorldTime ExpiresAt);
}
