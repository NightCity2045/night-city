// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Net;
using System.Numerics;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Neural;
using Content.Shared.DoAfter;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Neural;

[TestFixture]
public sealed class NCNeuralRuntimeHardeningTest
{
    [Test]
    public void VanillaAdapterDoesNotRejectStationWorkOwnedByAnotherSubscriber()
    {
        var action = new NCHumanActionPrototype();
        typeof(NCHumanActionPrototype).GetProperty(nameof(action.Executor))!
            .SetValue(action, NCHumanActionExecutorKind.VanillaStationWork);
        typeof(NCHumanActionPrototype).GetProperty(nameof(action.Operation))!
            .SetValue(action, NCHumanActionOperationKind.ProcessCargoOrder);
        var args = new NCHumanActionExecutionEvent(Guid.NewGuid(), NCActorId.New(), null,
            Guid.NewGuid(), Guid.NewGuid(), null!, null!, default, action, null!, default, default);
        var adapter = new NCHumanVanillaActionAdapterSystem();
        typeof(NCHumanVanillaActionAdapterSystem)
            .GetMethod("OnExecute", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(adapter, new object[] { args });
        Assert.Multiple(() =>
        {
            Assert.That(args.Handled, Is.False);
            Assert.That(args.Failure, Is.Null);
            Assert.That(args.Completed, Is.False);
        });
    }

    [Test]
    public void MalformedNestedProviderValuesRejectWithoutThrowing()
    {
        var request = Request();
        var valid = Response(request);
        NCNeuralResponseEnvelope[] malformed =
        [
            valid with { Commands = null! },
            valid with { Commands = [null!] },
            valid with { Intent = new NCNeuralIntentChoice("wait", null!, 1) },
            valid with { MemorySuggestions = [new NCNeuralMemorySuggestion("memory", null!, 1)] },
            valid with
            {
                DecisionMode = "sense",
                SenseRequests = [new NCNeuralSenseRequest("look-around", null!)],
            },
            valid with { SpeechActs = [null!] },
            valid with { ReasonTags = [null!] },
        ];

        foreach (var response in malformed)
        {
            Assert.DoesNotThrow(() => NCNeuralCognitionClientSystem.ValidResponseShape(request, response));
            Assert.That(NCNeuralCognitionClientSystem.ValidResponseShape(request, response), Is.False);
        }
    }

    [Test]
    public void ResponsePromptIdentityMustMatchRequest()
    {
        var request = Request();
        var response = Response(request);

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCognitionClientSystem.PromptIdentityMatches(request, response), Is.True);
            Assert.That(NCNeuralCognitionClientSystem.PromptIdentityMatches(
                request,
                response with { PromptVersion = response.PromptVersion + 1 }), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.PromptIdentityMatches(
                request,
                response with { EvaluationProfile = "other-evaluation" }), Is.False);
        });
    }

    [Test]
    public void ProcedureBatchRejectsEmptyDuplicateAndOversizedWithoutReindexing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralActionExecutionSystem.TryValidateProcedureBatch([(0, 0)], out var empty),
                Is.False);
            Assert.That(empty, Is.EqualTo("neural-command-procedure-empty"));

            Assert.That(NCNeuralActionExecutionSystem.TryValidateProcedureBatch([(0, 16), (4, 17)], out var large),
                Is.False);
            Assert.That(large, Is.EqualTo("neural-command-procedure-too-large"));

            Assert.That(NCNeuralActionExecutionSystem.TryValidateProcedureBatch([(3, 16), (7, 16)], out var valid),
                Is.True);
            Assert.That(valid, Is.Null);

            Assert.That(NCNeuralActionExecutionSystem.TryValidateProcedureBatch([(3, 1), (3, 1)], out var duplicate),
                Is.False);
            Assert.That(duplicate, Is.EqualTo("neural-command-correlation-invalid"));
        });
    }

    [Test]
    public void FilteringRejectsMalformedBatchAndKeepsProviderIndices()
    {
        var request = Request() with
        {
            Autonomy = new NCNeuralAutonomyContext("test", 0, 1000, true),
            AvailableActions =
            [
                new NCNeuralAvailableAction(
                    "NCCognitionHoldPosition",
                    "core",
                    "known-procedure",
                    ["execute"],
                    ["human-hold-position"],
                    ["hold-position"],
                    ["none"]),
            ],
        };
        NCNeuralCommandProposal[] malformed =
        [
            Command("invalid outcome key"),
            Command("position-held"),
        ];

        var rejected = NCNeuralCognitionClientSystem.SelectValidCommands(request, malformed);
        var selected = NCNeuralCognitionClientSystem.SelectValidCommands(request,
            [Command("position-held"), Command("position-held")]);

        Assert.Multiple(() =>
        {
            Assert.That(rejected, Is.Empty);
            Assert.That(selected, Has.Length.EqualTo(2));
            Assert.That(selected[0].OriginalIndex, Is.EqualTo(0));
            Assert.That(selected[1].OriginalIndex, Is.EqualTo(1));
        });
    }

    [Test]
    public void CommandResultKeepsRequestOriginalIndexCapabilityAndActualAction()
    {
        var requestId = Guid.NewGuid();
        var result = NCNeuralActionExecutionSystem.CreateCommandResult(
            requestId,
            5,
            "NCCognitionHoldPosition",
            "human-hold-position",
            "completed",
            "none",
            string.Empty,
            "execute",
            "position-held",
            new NCActiveWorldTime(100));
        var actorId = new NCActorId(Guid.NewGuid());
        var notification = new NCNeuralCommandResultEvent(actorId, result);

        Assert.Multiple(() =>
        {
            Assert.That(result.RequestId, Is.EqualTo(requestId));
            Assert.That(result.CommandIndex, Is.EqualTo(5));
            Assert.That(result.CapabilityId, Is.EqualTo("NCCognitionHoldPosition"));
            Assert.That(result.ActionId, Is.EqualTo("human-hold-position"));
            Assert.That(notification.ActorId, Is.EqualTo(actorId));
            Assert.That(notification.Result, Is.SameAs(result));
        });
    }

    [Test]
    public void AdapterConfirmedInteractionOperationsReportTerminalCompletion()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.ActivateTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.AltInteractTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.UseHeldTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.UseHeldOnTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.PickupTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.DropTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.EquipTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.ApproachTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.BuckleSelfToTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.StoreHeldInTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.ConsumeHeldTarget), Is.EqualTo("completed"));
            Assert.That(NCNeuralActionExecutionSystem.SuccessfulCommandState(
                NCHumanActionOperationKind.FireHeldGunAtTarget), Is.EqualTo("completed"));
        });
    }

    [Test]
    public void GenericUseHeldOnTargetWaitsForAuthoritativeDoAfterReceipt()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCHumanVanillaActionAdapterSystem.ClassifyInteractionReceipt(DoAfterStatus.Running),
                Is.EqualTo(NCHumanInteractionReceiptState.Pending));
            Assert.That(NCHumanVanillaActionAdapterSystem.ClassifyInteractionReceipt(DoAfterStatus.Finished),
                Is.EqualTo(NCHumanInteractionReceiptState.Completed));
            Assert.That(NCHumanVanillaActionAdapterSystem.ClassifyInteractionReceipt(DoAfterStatus.Cancelled),
                Is.EqualTo(NCHumanInteractionReceiptState.Failed));
            Assert.That(NCHumanVanillaActionAdapterSystem.ClassifyInteractionReceipt(DoAfterStatus.Invalid),
                Is.EqualTo(NCHumanInteractionReceiptState.Failed));
        });
    }

    [Test]
    public void ReloadCompletesOnlyAfterObservedAmmunitionChange()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCHumanVanillaActionAdapterSystem.IsReloadConfirmed(0, 1, true), Is.True);
            Assert.That(NCHumanVanillaActionAdapterSystem.IsReloadConfirmed(0, 0, false), Is.True);
            Assert.That(NCHumanVanillaActionAdapterSystem.IsReloadConfirmed(0, 0, true), Is.False);
        });
    }

    [Test]
    public void BatchTerminationReportsCurrentCommandAndSkipsEveryLaterCommandOnce()
    {
        var outcomes = NCNeuralActionExecutionSystem.BuildTerminalCommandOutcomes(
            [0, 0, 3, 3, 7],
            1,
            "failed");

        Assert.That(outcomes, Is.EqualTo(new[]
        {
            (StepIndex: 1, State: "failed"),
            (StepIndex: 2, State: "skipped"),
            (StepIndex: 4, State: "skipped"),
        }));
        Assert.That(NCNeuralActionExecutionSystem.BuildTerminalCommandOutcomes(
            [0],
            1,
            "interrupted"), Is.Empty);
    }

    [Test]
    public void CommandResultWakesOnlyWhenProviderSubscribed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCPhysicalActorBrainSystem.ShouldWakeForCommandResult(false), Is.False);
            Assert.That(NCPhysicalActorBrainSystem.ShouldWakeForCommandResult(true), Is.True);
        });
    }

    [Test]
    public void FailureReplanCorrelationSurvivesSenseAndActButClearsOnTerminalNonAction()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralContextBuilderSystem.ShouldClearFailureReplan(
                NCNeuralResultStatus.Accepted, "sense"), Is.False);
            Assert.That(NCNeuralContextBuilderSystem.ShouldClearFailureReplan(
                NCNeuralResultStatus.Accepted, "act"), Is.False);
            Assert.That(NCNeuralContextBuilderSystem.ShouldClearFailureReplan(
                NCNeuralResultStatus.Accepted, "wait"), Is.True);
            Assert.That(NCNeuralContextBuilderSystem.ShouldClearFailureReplan(
                NCNeuralResultStatus.Fallback, null), Is.True);
        });
    }

    [Test]
    public void ActorVersionFreshnessAllowsSpeechButNotStaleDialogueActions()
    {
        var dialogue = Request() with { Kind = NCNeuralRequestKind.DialogueContinuation };
        var speech = Response(dialogue);
        var action = speech with { Commands = [Command("position-held")] };
        var actionAndSpeech = action with
        {
            DecisionMode = "act",
            ShouldSpeak = true,
            SpeechActKey = "statement",
            RenderedText = "Иду.",
            SpeechActs = [new NCNeuralSpeechAct("statement", "Иду.")],
        };

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCognitionClientSystem.IsActorVersionFresh(
                dialogue, speech, dialogue.ContextVersion + 1), Is.True);
            Assert.That(NCNeuralCognitionClientSystem.IsActorVersionFresh(
                dialogue, action, dialogue.ContextVersion + 1), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.IsActorVersionFresh(
                dialogue, action, dialogue.ContextVersion), Is.True);
            Assert.That(NCNeuralCognitionClientSystem.IsActorVersionFresh(
                Request(), null, 2), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.TryKeepStaleDialogueSpeech(
                dialogue, actionAndSpeech, out var speechOnly), Is.True);
            Assert.That(speechOnly.Commands, Is.Empty);
            Assert.That(speechOnly.DecisionMode, Is.EqualTo("speak"));
            Assert.That(speechOnly.Intent, Is.Null);
        });
    }

    [Test]
    public void QueuedEnvelopeExpiresAfterPriorAcceptedDecisionRevision()
    {
        var request = Request() with { DecisionRevision = 4 };

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCognitionClientSystem.IsDecisionRevisionFresh(request, 4), Is.True);
            Assert.That(NCNeuralCognitionClientSystem.IsDecisionRevisionFresh(request, 5), Is.False);
        });
    }

    [Test]
    public void RequestSpecificRejectsDoNotAffectGlobalCircuit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCognitionClientSystem.CountsTowardCircuit(NCNeuralFailureKind.Schema), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.CountsTowardCircuit(
                NCNeuralFailureKind.ProviderRejected), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.ClassifyStatus(HttpStatusCode.BadRequest),
                Is.EqualTo(NCNeuralFailureKind.ProviderRejected));
            Assert.That(NCNeuralCognitionClientSystem.ClassifyStatus(HttpStatusCode.UnprocessableEntity),
                Is.EqualTo(NCNeuralFailureKind.ProviderRejected));
            Assert.That(NCNeuralCognitionClientSystem.CountsTowardCircuit(
                NCNeuralFailureKind.SessionCancelled), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.CountsTowardCircuit(
                NCNeuralFailureKind.Authentication), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.CountsTowardCircuit(NCNeuralFailureKind.Timeout), Is.True);
            Assert.That(NCNeuralCognitionClientSystem.CountsTowardCircuit(
                NCNeuralFailureKind.ServiceUnavailable), Is.True);
        });
    }

    [Test]
    public void LostRouteMayReturnUnavailableWithoutReintroducingLocationHandle()
    {
        var request = Request();
        var unavailable = new NCNeuralSenseResult(
            "examine-route",
            "station-destination:lost",
            "unavailable",
            [],
            null)
        {
            LocationHandles = [],
            DetailKeys = ["route.unavailable"],
        };
        var invalidCompleted = unavailable with
        {
            Status = "completed",
            DetailKeys = ["route.resolvable"],
        };

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCognitionClientSystem.ValidSenseResult(request, unavailable), Is.True);
            Assert.That(NCNeuralCognitionClientSystem.ValidSenseResult(request, invalidCompleted), Is.False);
        });
    }

    [Test]
    public void SkippedDependencyCommandResultIsValidTerminalContext()
    {
        var skipped = new NCNeuralCommandResult(
            "human-hold-position",
            "skipped",
            "dependency-failed",
            "prior-command-failed",
            100)
        {
            RequestId = Guid.NewGuid(),
            CapabilityId = "NCCognitionHoldPosition",
            CommandIndex = 1,
            GoalKey = "execute",
            ExpectedOutcomeKey = "position-held",
        };

        Assert.That(NCNeuralCognitionClientSystem.ValidCommandResult(skipped), Is.True);
    }

    [Test]
    public void PerceptionDeltaHistoryCannotRestoreDepartedTargetability()
    {
        var current = new NCNeuralPerceivedSubject("person-here", "unfamiliar person", "person-neutral", 1f, 500);
        var request = Request() with { PerceivedSubjects = [current] };
        var departed = new NCNeuralPerceptionDelta(
            "person-left", "person-gone", string.Empty, "neutral", "absent", false, false, 100, 500);
        var currentChange = new NCNeuralPerceptionDelta(
            "subject-state-changed", current.Handle, string.Empty, "neutral", "critical", true, false, 100, 500);

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCognitionClientSystem.ValidPerceptionDelta(request, departed), Is.True);
            Assert.That(NCNeuralCognitionClientSystem.ValidPerceptionDelta(
                request,
                departed with { SubjectTargetable = true }), Is.False);
            Assert.That(NCNeuralCognitionClientSystem.ValidPerceptionDelta(request, currentChange), Is.True);
        });
    }

    [Test]
    public void DefensiveReflexOffsetMovesAwayAndHandlesOverlappingBodies()
    {
        Assert.That(NCHumanVanillaActionAdapterSystem.TryGetDefensiveOffset(
            new Vector2(4f, 0f), Vector2.Zero, 2f, 0, out var offset), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(offset.X, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(offset.Y, Is.EqualTo(0f).Within(0.0001f));
        });

        Assert.That(NCHumanVanillaActionAdapterSystem.TryGetDefensiveOffset(
            Vector2.Zero, Vector2.Zero, 2f, 123, out var overlapOffset), Is.True);
        Assert.That(overlapOffset.Length(), Is.EqualTo(2f).Within(0.0001f));

        Assert.That(NCHumanVanillaActionAdapterSystem.TryGetDefensiveOffset(
            Vector2.Zero, Vector2.One, 0f, 0, out _), Is.False);
    }

    private static NCNeuralRequestEnvelope Request() =>
        new(
            Guid.NewGuid(),
            NCNeuralRequestKind.StrategicDecision,
            NCNeuralPriority.P1,
            new NCActorId(Guid.NewGuid()),
            NCSessionEpoch.New(),
            1,
            new NCActiveWorldTime(100),
            "NCLivingCityDefault",
            "living-city-default",
            3,
            "physical-gpt-alife-v3",
            [],
            ["NCCognitionHoldPosition"],
            [],
            "en-US");

    private static NCNeuralResponseEnvelope Response(NCNeuralRequestEnvelope request) =>
        new(
            request.RequestId,
            NCNeuralContract.SchemaVersion,
            request.ContextVersion,
            [],
            "statement",
            string.Empty,
            false,
            "wait",
            0,
            0,
            0,
            0,
            "provider-request",
            "provider-model",
            request.PromptStableKey,
            request.PromptVersion,
            request.EvaluationProfile)
        {
            DecisionMode = "wait",
        };

    private static NCNeuralCommandProposal Command(string expectedOutcomeKey) =>
        new(
            "NCCognitionHoldPosition",
            "execute",
            string.Empty,
            500,
            expectedOutcomeKey,
            [],
            []);
}
