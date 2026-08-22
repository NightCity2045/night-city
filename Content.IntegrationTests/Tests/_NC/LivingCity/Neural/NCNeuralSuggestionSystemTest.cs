// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Configuration;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Neural;

[TestFixture]
[TestOf(typeof(NCNeuralCognitionClientSystem))]
[TestOf(typeof(NCNeuralSuggestionSystem))]
[NonParallelizable]
public sealed class NCNeuralSuggestionSystemTest : GameTest
{
    private static readonly NCZoneId LittleChina =
        new(Guid.Parse("ff8d2122-6fb3-42cf-b3b4-02e83d9f908f"));
    private static readonly NCZoneId OldJapanTown =
        new(Guid.Parse("e2e35b70-5277-4359-91a6-0792e01fb477"));
    private static readonly ProtoId<NCCityActorArchetypePrototype> NeuralArchetype =
        new("NCLivingCityNeuralResident");
    private static readonly ProtoId<NCNeuralPromptProfilePrototype> Prompt =
        new("NCLivingCityDefault");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task DeterministicFallbackAndAcceptedProviderSuggestionShareOneSafeBdiBoundary()
    {
        var server = Pair.Server;
        var configuration = server.ResolveDependency<IConfigurationManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var neural = server.System<NCNeuralCognitionClientSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");
        Assert.That(clock.TryGetCurrentTime(out var epoch, out var now), Is.True);

        var created = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "neural-e2e-resident",
            "Mina Reyes",
            [],
            NeuralArchetype,
            LittleChina,
            OldJapanTown,
            Guid.NewGuid(),
            AuditSource: "integration.neural-e2e"));
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
        var actor = await actors.GetAsync(created.ActorId, true);
        Assert.That(actor, Is.Not.Null);

        var deterministic = Request(created.ActorId, epoch, actor!.AggregateVersion, now, Guid.NewGuid());
        Assert.Multiple(() =>
        {
            Assert.That(neural.IsProviderEnabled, Is.False);
            Assert.That(neural.Submit(deterministic), Is.True);
            Assert.That(neural.GetRuntimeSnapshot().BudgetMode, Is.EqualTo(NCNeuralBudgetMode.FallbackOnly));
        });
        await server.WaitRunTicks(2);
        NCCognitionFact? deterministicHint = null;
        await server.WaitPost(() => deterministicHint = CollectNeuralHint(server, actor, now));
        Assert.That(deterministicHint, Is.Null,
            "A deterministic fallback must never become a neural BDI hint.");

        actor = await actors.GetAsync(created.ActorId, true);
        Assert.That(actor, Is.Not.Null);
        Assert.That(clock.TryGetCurrentTime(out epoch, out now), Is.True);
        var acceptedRequest = Request(created.ActorId, epoch, actor!.AggregateVersion, now, Guid.NewGuid());
        var responseJson = $$"""
            {"requestId":"{{acceptedRequest.RequestId:D}}","schemaVersion":1,
             "contextVersion":{{acceptedRequest.ContextVersion}},
             "selectedCapabilityId":"NCCognitionStrategicFallback","speechActKey":"statement",
             "renderedText":"Я проверю безопасный вариант.","summaryKey":"safe-option","confidence":800,
             "inputTokens":40,"outputTokens":12,"providerRequestId":"stub-luna-response"}
            """;
        await using var provider = new OneShotHttpProvider(responseJson);
        await server.WaitPost(() =>
        {
            configuration.SetCVar(NCCVars.NeuralEnabled, true);
            configuration.SetCVar(NCCVars.NeuralEndpoint, provider.Endpoint);
            Assert.That(neural.Submit(acceptedRequest), Is.True);
        });
        await WaitUntil(server, () => neural.GetRuntimeSnapshot().Accepted == 1,
            "The validated provider response was not accepted.");

        NCCognitionFact? hint = null;
        await server.WaitPost(() => hint = CollectNeuralHint(server, actor, now));
        Assert.Multiple(() =>
        {
            Assert.That(hint, Is.Not.Null);
            Assert.That(hint!.Key, Is.EqualTo("social.utility-modifier.NCCognitionStrategicFallback"));
            Assert.That(hint.Source, Is.EqualTo("neural-suggestion"));
            Assert.That(hint.Value.Kind, Is.EqualTo(NCCognitionValueKind.Number));
            Assert.That(hint.Value.Number, Is.EqualTo(32f).Within(0.001f));
            Assert.That(neural.GetRuntimeSnapshot().ValidationRejects, Is.Zero);
        });

        await server.WaitPost(() =>
        {
            configuration.SetCVar(NCCVars.NeuralEnabled, false);
            configuration.SetCVar(NCCVars.NeuralEndpoint, string.Empty);
            server.EntMan.EventBus.RaiseEvent(
                EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound));
        });
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
    }

    private static NCNeuralRequestEnvelope Request(
        NCActorId actorId,
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch,
        long contextVersion,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now,
        Guid requestId) => new(
        requestId,
        NCNeuralRequestKind.StrategicDecision,
        NCNeuralPriority.P1,
        actorId,
        epoch,
        contextVersion,
        now.Advance(TimeSpan.FromMinutes(1)),
        Prompt,
        [new NCNeuralFact("core", "actor.can-act", actorId.ToString(), 1f, "Public")],
        ["NCCognitionStrategicFallback"],
        ["safe-option"],
        "ru-RU");

    private static NCCognitionFact? CollectNeuralHint(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCCityActorRecord actor,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        var sink = new NCCognitionFactSink(32);
        server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new NCCognitionCollectFactsEvent(actor.Snapshot.Identity.ActorId, actor, now, sink));
        return sink.ToArray().SingleOrDefault(value => value.Source == "neural-suggestion");
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 360)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }
        Assert.Fail(failure);
    }

    private sealed class OneShotHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _responseTask;

        public string Endpoint { get; }

        public OneShotHttpProvider(string responseJson)
        {
            _listener.Start();
            var endpoint = (IPEndPoint) _listener.LocalEndpoint;
            Endpoint = $"http://127.0.0.1:{endpoint.Port}/v1/cognition";
            _responseTask = RespondOnce(responseJson);
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _responseTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or
                                              ObjectDisposedException)
            {
                // Expected when an assertion stops the test before the one-shot provider receives its request.
            }
            _cancellation.Dispose();
        }

        private async Task RespondOnce(string responseJson)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var received = 0;
            while (received < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(received), _cancellation.Token);
                if (count == 0)
                    break;
                received += count;
                if (Encoding.ASCII.GetString(buffer, 0, received).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }

            var body = Encoding.UTF8.GetBytes(responseJson);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, _cancellation.Token);
            await stream.WriteAsync(body, _cancellation.Token);
        }
    }
}
