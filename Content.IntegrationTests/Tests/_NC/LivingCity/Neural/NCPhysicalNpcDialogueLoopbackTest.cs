// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server._NC.Atmos.Components;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared.Chat;
using Content.Shared.Bed.Sleep;
using Content.Shared.Atmos;
using Content.Shared.GameTicking;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Content.Shared.Speech;
using Content.Shared.Speech.Muting;
using Content.Shared._NC.Configuration;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Neural;

/// <summary>
/// Proves the production NPC speech loop at the actual HTTP boundary without loading the Reach acceptance fixture.
/// </summary>
[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCNeuralConversationSystem))]
public sealed class NCPhysicalNpcDialogueLoopbackTest : GameTest
{
    private static readonly ProtoId<NCCityActorArchetypePrototype> Archetype = new("NCLivingCityResident");
    private static readonly ProtoId<NCNeuralPersonaProfilePrototype> Persona = new("NCDefaultResidentPersona");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    [Test]
    public async Task GptSpeechWakesNearbyActorAndReplyUsesChatAuthority()
    {
        var server = Pair.Server;
        var maps = server.System<SharedMapSystem>();
        var atmosphere = server.System<AtmosphereSystem>();
        var entities = server.ResolveDependency<IEntityManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var lifecycle = server.System<NCPhysicalActorLifecycleSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var context = server.System<NCNeuralContextBuilderSystem>();
        var neural = server.System<NCNeuralCognitionClientSystem>();
        var speechProbe = server.System<NCNpcDialogueSpeechProbeSystem>();
        var speakerId = NCActorId.New();
        var listenerId = NCActorId.New();

        await using var provider = new NCNpcDialogueLoopbackProvider(speakerId, listenerId);
        server.CfgMan.SetCVar(NCCVars.NeuralEnabled, true);
        server.CfgMan.SetCVar(NCCVars.NeuralEndpoint, provider.Endpoint.ToString());
        server.CfgMan.SetCVar(NCCVars.NeuralApiKey, NCNpcDialogueLoopbackProvider.ApiKey);
        await server.WaitPost(() => entities.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && lifecycle.IsActive && neural.IsProviderEnabled,
            "The dialogue fixture did not start its active-world neural boundary.", 300);

        MapId mapId = default;
        await server.WaitAssertion(() =>
        {
            var map = maps.CreateMap(out mapId);
            entities.EnsureComponent<NCUniformAirComponent>(map);
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = 21.824779f;
            moles[(int) Gas.Nitrogen] = 82.10312f;
            atmosphere.SetMapAtmosphere(map, false, new GasMixture(moles, Atmospherics.T20C));
            var grid = maps.CreateGridEntity(mapId);
            var tiles = new List<(Vector2i, Tile)>(64);
            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 8; y++)
                    tiles.Add((new Vector2i(x, y), new Tile(1)));
            }
            maps.SetTiles(grid, tiles);
        });

        HumanoidCharacterProfile humanoid = null!;
        await server.WaitPost(() => humanoid = HumanoidCharacterProfile.Random());
        var payload = NCHumanoidProfilePayload.Serialize(serialization, humanoid);
        await CreateActor(actors, speakerId, "Mara Velez", payload);
        await CreateActor(actors, listenerId, "Inez Ward", payload);

        var speakerSpawn = await SpawnAt(server, lifecycle, speakerId,
            new MapCoordinates(new Vector2(3f, 3f), mapId));
        var listenerSpawn = await SpawnAt(server, lifecycle, listenerId,
            new MapCoordinates(new Vector2(4f, 3f), mapId));
        Assert.Multiple(() =>
        {
            Assert.That(speakerSpawn.Status, Is.EqualTo(NCPhysicalActorSpawnStatus.Spawned), speakerSpawn.Error);
            Assert.That(listenerSpawn.Status, Is.EqualTo(NCPhysicalActorSpawnStatus.Spawned), listenerSpawn.Error);
            Assert.That(speakerSpawn.EntityUid, Is.Not.Null);
            Assert.That(listenerSpawn.EntityUid, Is.Not.Null);
        });
        var speaker = speakerSpawn.EntityUid!.Value;
        var listener = listenerSpawn.EntityUid!.Value;
        await server.WaitAssertion(() =>
        {
            entities.EnsureComponent<NCNpcDialogueSpeechProbeComponent>(speaker);
            entities.EnsureComponent<NCNpcDialogueSpeechProbeComponent>(listener);
            Assert.That(runtime.TryGetActor(speaker, out var actualSpeaker), Is.True);
            Assert.That(actualSpeaker, Is.EqualTo(speakerId));
            Assert.That(runtime.TryGetActor(listener, out var actualListener), Is.True);
            Assert.That(actualListener, Is.EqualTo(listenerId));
            Assert.That(entities.HasComponent<NCNeuralConversationTargetComponent>(speaker), Is.True);
            Assert.That(entities.HasComponent<NCNeuralConversationTargetComponent>(listener), Is.True);
        });

        // Drain any lifecycle bootstrap decision before arming the deterministic autonomous line. Lifecycle wake
        // cadence is intentionally independent per actor, so this fixture does not require both to wake first.
        await WaitUntil(server,
            () => neural.GetRuntimeSnapshot() is { Queued: 0, InFlight: 0 },
            "The bootstrap decision did not settle.", 600,
            () => Diagnostics(provider, speechProbe, speaker, listener, speakerId, listenerId, entities, runtime,
                context, neural));
        speechProbe.Clear();
        provider.Arm();
        Task<bool> submission = null!;
        await server.WaitPost(() => submission = context.SubmitAutonomousAsync(speakerId, NCNeuralPriority.P2));
        await WaitUntil(server, () => submission.IsCompleted,
            "The speaker context build did not complete.", 300);
        Assert.That(await submission, Is.True, "The armed speaker request was not accepted by the transport queue.");

        await WaitUntil(server,
            () => provider.GetSnapshot().ListenerDialogueRequests > 0 &&
                  speechProbe.Contains(speaker, NCNpcDialogueLoopbackProvider.OpeningLine) &&
                  speechProbe.Contains(listener, NCNpcDialogueLoopbackProvider.ReplyLine),
            "The GPT line did not produce an actor-isolated dialogue request and physical ChatSystem reply.",
            900,
            () => Diagnostics(provider, speechProbe, speaker, listener, speakerId, listenerId, entities, runtime,
                context, neural));

        await server.WaitAssertion(() =>
        {
            var snapshot = provider.GetSnapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ListenerDialogueRequests, Is.EqualTo(1));
                Assert.That(snapshot.LastListenerUtterance, Is.EqualTo(NCNpcDialogueLoopbackProvider.OpeningLine));
                Assert.That(snapshot.LastListenerActor, Is.EqualTo(listenerId));
                Assert.That(context.OrdinaryDialogueSubmissions, Is.GreaterThanOrEqualTo(1));
                Assert.That(speechProbe.Messages(speaker), Does.Contain(NCNpcDialogueLoopbackProvider.OpeningLine));
                Assert.That(speechProbe.Messages(listener), Does.Contain(NCNpcDialogueLoopbackProvider.ReplyLine));
            });
        });
    }

    private static async Task CreateActor(
        NCCityActorRegistrySystem actors,
        NCActorId actorId,
        string name,
        string profile)
    {
        var commandId = Guid.NewGuid();
        var result = await actors.CreateResidentAsync(new NCCityResidentCreateRequest(
            commandId,
            actorId,
            name,
            Archetype,
            Persona,
            profile,
            default,
            null,
            commandId,
            AuditSource: "integration.neural-npc-dialogue"));
        Assert.That(result.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), result.Error);
    }

    private static async Task<NCPhysicalActorSpawnState> SpawnAt(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCPhysicalActorLifecycleSystem lifecycle,
        NCActorId actorId,
        MapCoordinates coordinates)
    {
        Task<NCPhysicalActorSpawnState> completion = null!;
        await server.WaitPost(() => completion = lifecycle.RequestAdminAsync(
            Guid.NewGuid(), actorId, coordinates, "integration.neural-npc-dialogue"));
        await WaitUntil(server, () => completion.IsCompleted,
            $"Actor {actorId} did not finish physical spawn.", 300);
        return await completion;
    }

    private static string Diagnostics(
        NCNpcDialogueLoopbackProvider provider,
        NCNpcDialogueSpeechProbeSystem speech,
        EntityUid speaker,
        EntityUid listener,
        NCActorId speakerId,
        NCActorId listenerId,
        IEntityManager entities,
        NCCityActorRuntimeIndexSystem runtimeIndex,
        NCNeuralContextBuilderSystem context,
        NCNeuralCognitionClientSystem neural)
    {
        var snapshot = provider.GetSnapshot();
        var runtime = neural.GetRuntimeSnapshot();
        neural.TryGetActorRuntimeSnapshot(listenerId, out var listenerNeural);
        runtimeIndex.TryGetEntity(speakerId, out var currentSpeaker);
        runtimeIndex.TryGetEntity(listenerId, out var currentListener);
        var listenerState = "missing";
        if (entities.TryGetComponent(listener, out MobStateComponent? mob))
        {
            var state = mob.CurrentState;
            listenerState = state.ToString();
        }
        var listenerSpeech = "missing";
        if (entities.TryGetComponent(listener, out SpeechComponent? speechComponent))
        {
            var enabled = speechComponent.Enabled;
            listenerSpeech = enabled.ToString();
        }
        var listenerBodySpeech = "missing";
        if (entities.TryGetComponent(listener, out NCBodyFunctionComponent? bodyFunction))
        {
            var function = bodyFunction.Speech;
            listenerBodySpeech = function.ToString(CultureInfo.InvariantCulture);
        }
        return $"requests={snapshot.TotalRequests}, listener-dialogue={snapshot.ListenerDialogueRequests}, " +
               $"speaker-chat=[{string.Join("|", speech.Messages(speaker))}], " +
               $"listener-chat=[{string.Join("|", speech.Messages(listener))}], " +
               $"speaker-attempts={speech.SpeakAttempts(speaker)}, listener-attempts={speech.SpeakAttempts(listener)}, " +
               $"current-bodies={currentSpeaker}/{currentListener}, original-bodies={speaker}/{listener}, " +
               $"listener-state={listenerState}, speech={listenerSpeech}, body-speech={listenerBodySpeech}, " +
               $"sleeping={entities.HasComponent<SleepingComponent>(listener)}, " +
               $"muted={entities.HasComponent<MutedComponent>(listener)}, " +
               $"listener-result={listenerNeural?.LastRequestKind}/{listenerNeural?.LastStatus}/" +
               $"{listenerNeural?.LastReason}/{listenerNeural?.LastSummaryKey}, " +
               $"neural-results=[{string.Join("|", speech.NeuralResults)}], " +
               $"turns=[{string.Join("|", speech.ConversationTurns)}], " +
               $"dialogue-submissions={context.OrdinaryDialogueSubmissions}, " +
               $"queued={runtime.Queued}, in-flight={runtime.InFlight}, " +
               $"last-failure={runtime.LastFailureKind}:{runtime.LastFailureReason}";
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int maximumTicks,
        Func<string>? diagnostics = null)
    {
        for (var tick = 0; tick < maximumTicks; tick++)
        {
            if (predicate())
                return;
            await server.WaitRunTicks(1);
            await Task.Delay(1);
        }
        Assert.Fail(diagnostics == null ? failure : $"{failure} {diagnostics()}");
    }
}

/// <summary>Records only speech which crossed ChatSystem and raised the normal entity-spoke event.</summary>
public sealed class NCNpcDialogueSpeechProbeSystem : EntitySystem
{
    private readonly Dictionary<EntityUid, List<string>> _messages = [];
    private readonly Dictionary<EntityUid, int> _speakAttempts = [];

    public readonly List<string> NeuralResults = [];
    public readonly List<string> ConversationTurns = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNpcDialogueSpeechProbeComponent, EntitySpokeEvent>(OnSpoke);
        SubscribeLocalEvent<NCNpcDialogueSpeechProbeComponent, SpeakAttemptEvent>(OnSpeakAttempt);
        SubscribeLocalEvent<NCNeuralResultEvent>(OnNeuralResult);
        SubscribeLocalEvent<NCConversationTurnRecordedEvent>(OnConversationTurn);
    }

    public void Clear()
    {
        _messages.Clear();
        _speakAttempts.Clear();
        NeuralResults.Clear();
        ConversationTurns.Clear();
    }

    public IReadOnlyList<string> Messages(EntityUid speaker) =>
        _messages.TryGetValue(speaker, out var messages) ? messages : [];

    public bool Contains(EntityUid speaker, string message) => Messages(speaker).Contains(message);

    public int SpeakAttempts(EntityUid speaker) => _speakAttempts.GetValueOrDefault(speaker);

    private void OnSpoke(Entity<NCNpcDialogueSpeechProbeComponent> entity, ref EntitySpokeEvent args)
    {
        if (!_messages.TryGetValue(entity.Owner, out var messages))
        {
            messages = [];
            _messages.Add(entity.Owner, messages);
        }
        messages.Add(args.Message);
    }

    private void OnSpeakAttempt(Entity<NCNpcDialogueSpeechProbeComponent> entity, ref SpeakAttemptEvent args) =>
        _speakAttempts[entity.Owner] = _speakAttempts.GetValueOrDefault(entity.Owner) + 1;

    private void OnNeuralResult(NCNeuralResultEvent args)
    {
        if (args.Result.Request.Kind != NCNeuralRequestKind.DialogueContinuation)
            return;
        NeuralResults.Add($"{args.Result.Request.ActorId}:{args.Result.Status}:" +
                          $"{args.Result.Response?.DecisionMode}:{args.Result.Response?.RenderedText}");
    }

    private void OnConversationTurn(NCConversationTurnRecordedEvent args)
    {
        var turn = args.Session.Turns.LastOrDefault();
        if (turn != null)
            ConversationTurns.Add($"{turn.Speaker}:{args.TransientUtterance}");
    }
}

/// <summary>Test-only marker that gives the speech probe a unique event subscription pair.</summary>
[RegisterComponent]
public sealed partial class NCNpcDialogueSpeechProbeComponent : Component;

/// <summary>Small strict loopback which authors one opening line and one listener reply.</summary>
internal sealed class NCNpcDialogueLoopbackProvider : IAsyncDisposable
{
    public const string ApiKey = "npc-dialogue-loopback-key";
    public const string OpeningLine = "Hey, neighbor.";
    public const string ReplyLine = "Yeah. I'm listening.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NCActorId _speaker;
    private readonly NCActorId _listenerActor;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly HashSet<NCActorId> _seenActors = [];
    private readonly object _sync = new();
    private readonly Task _acceptLoop;
    private bool _armed;
    private bool _openingSent;
    private int _totalRequests;
    private int _listenerDialogueRequests;
    private int _sequence;
    private string _lastListenerUtterance = string.Empty;
    private NCActorId _lastListenerActor;

    public NCNpcDialogueLoopbackProvider(NCActorId speaker, NCActorId listener)
    {
        _speaker = speaker;
        _listenerActor = listener;
        _listener.Start();
        Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint) _listener.LocalEndpoint).Port}/v1/cognition");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri Endpoint { get; }

    public void Arm()
    {
        lock (_sync)
            _armed = true;
    }

    public NCNpcDialogueLoopbackSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new NCNpcDialogueLoopbackSnapshot(
                _totalRequests,
                _seenActors.ToHashSet(),
                _listenerDialogueRequests,
                _lastListenerUtterance,
                _lastListenerActor);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
        {
        }
        await Task.WhenAll(_handlers.ToArray());
        _cancellation.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                _handlers.Add(HandleAsync(client));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                var reader = new BufferedNetworkReader(stream);
                if (await reader.ReadLineAsync(_cancellation.Token) != "POST /v1/cognition HTTP/1.1")
                {
                    await WriteResponseAsync(stream, HttpStatusCode.BadRequest, null);
                    return;
                }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(_cancellation.Token) is { } line && line.Length > 0)
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0)
                        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
                if (!headers.TryGetValue("Authorization", out var authorization) ||
                    authorization != $"Bearer {ApiKey}")
                {
                    await WriteResponseAsync(stream, HttpStatusCode.Unauthorized, null);
                    return;
                }

                byte[] payload;
                if (headers.TryGetValue("Content-Length", out var lengthValue) &&
                    int.TryParse(lengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out var length) &&
                    length >= 0)
                {
                    payload = await reader.ReadExactlyAsync(length, _cancellation.Token);
                }
                else if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                         transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    payload = await reader.ReadChunkedAsync(_cancellation.Token);
                }
                else
                {
                    await WriteResponseAsync(stream, HttpStatusCode.BadRequest, null);
                    return;
                }

                using var document = JsonDocument.Parse(payload);
                if (!TryReadRequest(document.RootElement, out var request))
                {
                    await WriteResponseAsync(stream, HttpStatusCode.UnprocessableEntity, null);
                    return;
                }

                NCNeuralResponseEnvelope response;
                lock (_sync)
                {
                    _totalRequests++;
                    _seenActors.Add(request.ActorId);
                    var text = string.Empty;
                    if (_armed && !_openingSent && request.ActorId == _speaker &&
                        request.Kind == nameof(NCNeuralRequestKind.StrategicDecision))
                    {
                        _openingSent = true;
                        text = OpeningLine;
                    }
                    else if (request.ActorId == _listenerActor &&
                             request.Kind == nameof(NCNeuralRequestKind.DialogueContinuation))
                    {
                        _listenerDialogueRequests++;
                        _lastListenerActor = request.ActorId;
                        _lastListenerUtterance = request.Utterance;
                        text = ReplyLine;
                    }
                    response = CreateResponse(request, text, ++_sequence);
                }
                await WriteResponseAsync(stream, HttpStatusCode.OK, response);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch
            {
                try
                {
                    await WriteResponseAsync(stream, HttpStatusCode.InternalServerError, null);
                }
                catch
                {
                    // Test disposal or a closed client may make the diagnostic response impossible.
                }
            }
        }
    }

    private static NCNeuralResponseEnvelope CreateResponse(LoopbackRequest request, string speech, int sequence)
    {
        var speaks = speech.Length > 0;
        return new NCNeuralResponseEnvelope(
            request.RequestId,
            request.SchemaVersion,
            request.ContextVersion,
            [],
            "statement",
            speech,
            speaks,
            speaks ? "npc-dialogue-reply" : "npc-dialogue-wait",
            900,
            8,
            0,
            4,
            $"npc-dialogue-loopback-{sequence}",
            "gpt-5.6-luna",
            request.PromptStableKey,
            request.PromptVersion,
            request.EvaluationProfile,
            [],
            [])
        {
            DecisionMode = speaks ? "speak" : "wait",
            SpeechActs = speaks ? [new NCNeuralSpeechAct("statement", speech)] : [],
            ReasonTags = [speaks ? "dialogue-response" : "no-immediate-action"],
        };
    }

    private static bool TryReadRequest(JsonElement root, out LoopbackRequest request)
    {
        request = default;
        if (!root.TryGetProperty("schemaVersion", out var schema) ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != NCNeuralContract.SchemaVersion ||
            !root.TryGetProperty("requestId", out var requestIdElement) ||
            !requestIdElement.TryGetGuid(out var requestId) ||
            !root.TryGetProperty("actorId", out var actorElement) ||
            !actorElement.TryGetProperty("value", out var actorValue) ||
            !actorValue.TryGetGuid(out var actorGuid) ||
            !root.TryGetProperty("kind", out var kindElement) || kindElement.GetString() is not { } kind ||
            !root.TryGetProperty("contextVersion", out var contextElement) ||
            !contextElement.TryGetInt64(out var contextVersion) ||
            !root.TryGetProperty("promptStableKey", out var promptStableElement) ||
            promptStableElement.GetString() is not { Length: > 0 } promptStableKey ||
            !root.TryGetProperty("promptVersion", out var promptVersionElement) ||
            !promptVersionElement.TryGetInt32(out var promptVersion) ||
            !root.TryGetProperty("evaluationProfile", out var evaluationElement) ||
            evaluationElement.GetString() is not { Length: > 0 } evaluationProfile ||
            !root.TryGetProperty("perceptionDeltas", out var deltas) ||
            deltas.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("perceivedSubjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array ||
            subjects.EnumerateArray().Any(subject => !subject.TryGetProperty("identityKnown", out _)))
        {
            return false;
        }

        var utterance = string.Empty;
        if (root.TryGetProperty("dialogueInput", out var dialogue) && dialogue.ValueKind == JsonValueKind.Object &&
            dialogue.TryGetProperty("utterance", out var utteranceElement))
            utterance = utteranceElement.GetString() ?? string.Empty;
        request = new LoopbackRequest(
            schemaVersion,
            requestId,
            new NCActorId(actorGuid),
            kind,
            contextVersion,
            promptStableKey,
            promptVersion,
            evaluationProfile,
            utterance);
        return true;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode status,
        NCNeuralResponseEnvelope? response)
    {
        var payload = response == null ? [] : JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var reason = status switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.UnprocessableEntity => "Unprocessable Entity",
            _ => "Internal Server Error",
        };
        var contentType = payload.Length == 0 ? string.Empty : "Content-Type: application/json\r\n";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int) status} {reason}\r\n{contentType}Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        if (payload.Length > 0)
            await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private sealed class BufferedNetworkReader(NetworkStream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _offset;
        private int _count;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (line.Length <= 65536)
            {
                var value = await ReadByteAsync(cancellationToken);
                if (value < 0)
                    return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                if (value == '\n')
                    return Encoding.ASCII.GetString(line.ToArray());
                if (value != '\r')
                    line.WriteByte((byte) value);
            }
            throw new InvalidDataException("HTTP header line exceeded the test provider limit.");
        }

        public async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
        {
            var result = new byte[length];
            var written = 0;
            while (written < length)
            {
                if (_offset == _count)
                    await FillAsync(cancellationToken);
                var copy = Math.Min(length - written, _count - _offset);
                if (copy == 0)
                    throw new EndOfStreamException();
                _buffer.AsSpan(_offset, copy).CopyTo(result.AsSpan(written));
                _offset += copy;
                written += copy;
            }
            return result;
        }

        public async Task<byte[]> ReadChunkedAsync(CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
                var extension = sizeLine.IndexOf(';');
                var sizeText = extension < 0 ? sizeLine : sizeLine[..extension];
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) ||
                    size < 0)
                    throw new InvalidDataException("Invalid HTTP chunk length.");
                if (size == 0)
                {
                    while (await ReadLineAsync(cancellationToken) is { Length: > 0 })
                    {
                    }
                    return body.ToArray();
                }
                var chunk = await ReadExactlyAsync(size, cancellationToken);
                await body.WriteAsync(chunk, cancellationToken);
                if (await ReadLineAsync(cancellationToken) != string.Empty)
                    throw new InvalidDataException("Invalid HTTP chunk terminator.");
            }
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_offset == _count)
                await FillAsync(cancellationToken);
            return _offset == _count ? -1 : _buffer[_offset++];
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            _offset = 0;
            _count = await stream.ReadAsync(_buffer, cancellationToken);
        }
    }

    private readonly record struct LoopbackRequest(
        int SchemaVersion,
        Guid RequestId,
        NCActorId ActorId,
        string Kind,
        long ContextVersion,
        string PromptStableKey,
        int PromptVersion,
        string EvaluationProfile,
        string Utterance);
}

internal sealed record NCNpcDialogueLoopbackSnapshot(
    int TotalRequests,
    IReadOnlySet<NCActorId> SeenActors,
    int ListenerDialogueRequests,
    string LastListenerUtterance,
    NCActorId LastListenerActor);
