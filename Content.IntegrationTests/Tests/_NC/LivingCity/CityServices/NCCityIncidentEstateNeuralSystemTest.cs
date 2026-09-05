// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Lifecycle;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Lifecycle;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.CityServices;

[TestFixture]
[TestOf(typeof(NCCityIncidentRegistrySystem))]
[TestOf(typeof(NCCityServiceSystem))]
[TestOf(typeof(NCEstateRegistrySystem))]
[TestOf(typeof(NCNeuralCognitionClientSystem))]
[NonParallelizable]
public sealed class NCCityIncidentEstateNeuralSystemTest : GameTest
{
    [Test]
    public async Task IncidentServiceEstateAndNeuralAdmissionAreRoundBoundAndReplaySafe()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var incidents = server.System<NCCityIncidentRegistrySystem>();
        var services = server.System<NCCityServiceSystem>();
        var news = server.System<NCCityNewsRegistrySystem>();
        var estates = server.System<NCEstateRegistrySystem>();
        var neural = server.System<NCNeuralCognitionClientSystem>();
        var reporter = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var beneficiary = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && services.CachedServiceCount >= 4,
            "The round services did not bootstrap.");
        Assert.That(clock.TryGetCurrentTime(out var epoch, out var now), Is.True);

        var traumaParty = NCPersistentIdentity.CityService("trauma-team");
        var traumaId = new NCCityServiceId(traumaParty.PersistentId);
        var trauma = await services.GetServiceAsync(traumaId, true);
        Assert.That(trauma, Is.Not.Null);
        var contractRequest = new NCCreateServiceContractRequest(Guid.NewGuid(),
            new NCServiceContractId(Guid.NewGuid()), traumaId, reporter, 2, 25_000, 6_000, null,
            traumaParty, Guid.NewGuid(), AuditSource: "phase-thirteen-test");
        Assert.That((await services.CreateContractAsync(contractRequest)).Status,
            Is.EqualTo(NCCityServiceMutationStatus.Applied));
        Assert.That((await services.CreateContractAsync(contractRequest)).Status,
            Is.EqualTo(NCCityServiceMutationStatus.Duplicate));

        var sourceEvent = Guid.NewGuid();
        var incidentId = new NCCityIncidentId(Guid.NewGuid());
        var createIncident = new NCCreateIncidentRequest(Guid.NewGuid(), incidentId,
            NCCityIncidentKind.MedicalEmergency, 400, new NCZoneId(Guid.NewGuid()),
            [new NCIncidentParticipant(reporter, NCIncidentParticipantRole.Reporter, true)],
            new NCIncidentCause(sourceEvent, "integration", "medical-emergency", "patient-1"),
            NCIncidentMaterializationPolicy.AbstractUnlessObserved, TimeSpan.FromMinutes(5), reporter,
            Guid.NewGuid(), AuditSource: "phase-thirteen-test");
        var created = await incidents.CreateAsync(createIncident);
        Assert.That(created.Status, Is.EqualTo(NCIncidentMutationStatus.Applied));
        Assert.That((await incidents.CreateAsync(createIncident)).Status, Is.EqualTo(NCIncidentMutationStatus.Duplicate));

        var evidence = await incidents.AddEvidenceAsync(new NCAddIncidentEvidenceRequest(Guid.NewGuid(), incidentId,
            created.Incident!.AggregateVersion,
            new NCIncidentEvidence(Guid.NewGuid(), NCIncidentEvidenceKind.Medical, reporter, "triage-record", 900,
                true, default), reporter, Guid.NewGuid(), AuditSource: "phase-thirteen-test"));
        Assert.That(evidence.Status, Is.EqualTo(NCIncidentMutationStatus.Applied));
        var resolved = await incidents.ResolveAsync(new NCResolveIncidentRequest(Guid.NewGuid(), incidentId,
            evidence.Incident!.AggregateVersion, new NCIncidentOutcome("stabilized", [], [], 0, 500), reporter,
            Guid.NewGuid(), AuditSource: "phase-thirteen-test"));
        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.EqualTo(NCIncidentMutationStatus.Applied));
            Assert.That(resolved.Incident?.Snapshot.Stage, Is.EqualTo(NCCityIncidentStage.Resolved));
            Assert.That(resolved.Incident?.Snapshot.Evidence.Count, Is.EqualTo(1));
        });
        await WaitUntilAsync(server, async () => (await news.GetPageAsync(null, 10)).Count >= 1,
            "Resolution news was not published.");

        // A legacy active incident may still carry an old abstract-resolution deadline in durable data. Reaching
        // that deadline must only clear it; no timer is allowed to fabricate casualties, loss, or an outcome.
        var heldIncidentId = new NCCityIncidentId(Guid.NewGuid());
        var heldCreated = await incidents.CreateAsync(createIncident with
        {
            CommandId = Guid.NewGuid(),
            IncidentId = heldIncidentId,
            Cause = createIncident.Cause with { SourceWorldEventId = Guid.NewGuid(), ReferenceId = "patient-2" },
            CorrelationId = Guid.NewGuid(),
        });
        Assert.That(heldCreated.Status, Is.EqualTo(NCIncidentMutationStatus.Applied));
        var heldVerified = await incidents.ChangeStageAsync(new NCChangeIncidentStageRequest(
            Guid.NewGuid(), heldIncidentId, heldCreated.Incident!.AggregateVersion, NCCityIncidentStage.Verified,
            reporter, now.Advance(TimeSpan.FromHours(1)), Guid.NewGuid(), AuditSource: "phase-thirteen-test"));
        Assert.That(heldVerified.Status, Is.EqualTo(NCIncidentMutationStatus.Applied));
        var heldActive = await incidents.ChangeStageAsync(new NCChangeIncidentStageRequest(
            Guid.NewGuid(), heldIncidentId, heldVerified.Incident!.AggregateVersion, NCCityIncidentStage.Active,
            reporter, now, Guid.NewGuid(), AuditSource: "phase-thirteen-test"));
        Assert.That(heldActive.Status, Is.EqualTo(NCIncidentMutationStatus.Applied));
        await WaitUntilAsync(server, async () =>
        {
            var current = await incidents.GetAsync(heldIncidentId, true);
            return current?.Snapshot is
            {
                Stage: NCCityIncidentStage.Active,
                EscalatesAt: null,
                Outcome: null,
            };
        }, "The incident lifecycle did not remove the legacy automatic-resolution deadline.");

        var deathEvent = Guid.NewGuid();
        var estate = await estates.OpenAsync(reporter, beneficiary, deathEvent, "Reporter estate");
        Assert.That(estate, Is.Not.Null);
        Assert.That((await estates.OpenAsync(reporter, beneficiary, deathEvent, "Reporter estate"))?.Snapshot.EstateId,
            Is.EqualTo(estate!.Snapshot.EstateId));
        var withAsset = await estates.AddAssetAsync(estate.Snapshot.EstateId, estate.AggregateVersion,
            new NCEstateAssetRef(NCEstateAssetKind.Item, "item:test-heirloom", 500, false));
        var claimId = Guid.NewGuid();
        var withClaim = await estates.FileClaimAsync(estate.Snapshot.EstateId, withAsset!.AggregateVersion,
            new NCEstateClaim(claimId, beneficiary, 100, "funeral-cost", NCEstateClaimStatus.Filed, now));
        Assert.That(await estates.CloseAsync(estate.Snapshot.EstateId, withClaim!.AggregateVersion), Is.Null,
            "An estate with unresolved assets and claims must remain open.");
        var transferred = await estates.SetAssetTransferredAsync(estate.Snapshot.EstateId, withClaim.AggregateVersion,
            NCEstateAssetKind.Item, "item:test-heirloom");
        var paid = await estates.SetClaimStatusAsync(estate.Snapshot.EstateId, transferred!.AggregateVersion, claimId,
            NCEstateClaimStatus.Paid);
        var closed = await estates.CloseAsync(estate.Snapshot.EstateId, paid!.AggregateVersion);
        Assert.That(closed?.Snapshot.Status, Is.EqualTo(NCEstateStatus.Closed));

        var request = new NCNeuralRequestEnvelope(Guid.NewGuid(), NCNeuralRequestKind.StrategicDecision,
            NCNeuralPriority.P1, NCActorId.New(), epoch, 1, now.Advance(TimeSpan.FromMinutes(1)),
            new ProtoId<NCNeuralPromptProfilePrototype>("NCLivingCityDefault"),
            "living-city-default", 1, "physical-gpt-alife-v3",
            [new NCNeuralFact("incident", "resolved", incidentId.ToString(), 1f, "public", "incident-resolved")],
            ["physical.wait"], ["medical"], "en-US",
            Autonomy: new NCNeuralAutonomyContext("ordinary", 0, 1000, false),
            Disclosure: new NCNeuralDisclosureContext(
                "public",
                ["incident-resolved"],
                ["incident-resolved"],
                [],
                []));
        Assert.That(neural.Submit(request), Is.True);
        Assert.That(neural.Submit(request), Is.False, "A neural request id must be idempotent.");
        Assert.Multiple(() =>
        {
            var runtime = neural.GetRuntimeSnapshot();
            Assert.That(runtime.Active, Is.True);
            Assert.That(runtime.ProviderConfigured, Is.False);
            Assert.That(runtime.Fallbacks, Is.Zero,
                "Missing provider configuration must not masquerade as a token quota rejection.");
            Assert.That(runtime.Queued, Is.EqualTo(1),
                "Admitted work stays pending until the provider becomes available or the round closes.");
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(IServerDbManager database)
    {
        var preference = await database.InitPrefsAsync(new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile(), CancellationToken.None);
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private static async Task WaitUntil(RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate, string failure, int attempts = 240)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete) return;
        }
        Assert.Fail(failure);
    }

    private static async Task WaitUntilAsync(RobustIntegrationTest.ServerIntegrationInstance server,
        Func<Task<bool>> predicate, string failure, int attempts = 240)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await server.WaitRunTicks(1);
            if (await predicate()) return;
        }
        Assert.Fail(failure);
    }
}
