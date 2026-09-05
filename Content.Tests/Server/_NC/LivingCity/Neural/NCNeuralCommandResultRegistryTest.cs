// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Server._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Neural;

[TestFixture]
public sealed class NCNeuralCommandResultRegistryTest
{
    [Test]
    public void DurableHistoryAcceptsOnlyBoundedTerminalResults()
    {
        var actorId = NCActorId.New();
        var terminal = Result(Guid.NewGuid(), 2, "completed", 100);
        var accepted = Result(Guid.NewGuid(), 3, "accepted", 101);
        var skipped = Result(Guid.NewGuid(), 4, "skipped", 102);

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCommandResultRegistrySystem.TryValidateTerminal(actorId, terminal), Is.True);
            Assert.That(NCNeuralCommandResultRegistrySystem.TryValidateTerminal(actorId, accepted), Is.False);
            Assert.That(NCNeuralCommandResultRegistrySystem.TryValidateTerminal(actorId, skipped), Is.True);
            Assert.That(NCNeuralCommandResultRegistrySystem.TryValidateTerminal(default, terminal), Is.False);
            Assert.That(NCNeuralCommandResultRegistrySystem.TryValidateTerminal(
                actorId,
                terminal with { RequestId = Guid.Empty }), Is.False);
        });

        IReadOnlyList<NCNeuralCommandResult> history = [];
        for (var index = 0; index < 10; index++)
        {
            Assert.That(NCNeuralCommandResultRegistrySystem.TryAppendTerminal(
                actorId,
                history,
                Result(Guid.NewGuid(), index % 8, "completed", index),
                out var merged,
                out var changed), Is.True);
            Assert.That(changed, Is.True);
            history = merged;
        }

        Assert.Multiple(() =>
        {
            Assert.That(history, Has.Count.EqualTo(8));
            Assert.That(history[0].UpdatedAtTicks, Is.EqualTo(2));
            Assert.That(history[^1].UpdatedAtTicks, Is.EqualTo(9));
        });
    }

    [Test]
    public void DuplicateIdentityIsIdempotentButConflictingPayloadFailsClosed()
    {
        var actorId = NCActorId.New();
        var result = Result(Guid.NewGuid(), 1, "failed", 100);

        Assert.That(NCNeuralCommandResultRegistrySystem.TryAppendTerminal(
            actorId,
            [result],
            result,
            out var duplicate,
            out var changed), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(duplicate, Is.EqualTo(new[] { result }));
        });

        Assert.That(NCNeuralCommandResultRegistrySystem.TryAppendTerminal(
            actorId,
            [result],
            result with { ActionId = "human-other-action" },
            out _,
            out _), Is.False);
    }

    [Test]
    public void PersistenceIdentityUsesRequestIndexStateAndRetryAttemptDeterministically()
    {
        var result = Result(Guid.NewGuid(), 4, "interrupted", 100);
        var first = NCNeuralCommandResultRegistrySystem.BuildPersistenceCommandId(result);

        Assert.Multiple(() =>
        {
            Assert.That(NCNeuralCommandResultRegistrySystem.BuildPersistenceCommandId(result), Is.EqualTo(first));
            Assert.That(NCNeuralCommandResultRegistrySystem.BuildPersistenceCommandId(
                result with { CommandIndex = 5 }), Is.Not.EqualTo(first));
            Assert.That(NCNeuralCommandResultRegistrySystem.BuildPersistenceCommandId(
                result with { State = "failed" }), Is.Not.EqualTo(first));
            Assert.That(NCNeuralCommandResultRegistrySystem.BuildPersistenceCommandId(result, 1),
                Is.Not.EqualTo(first));
        });
    }

    [Test]
    public void ContextMergeKeepsDurableAndRuntimeResultsWithoutDuplicates()
    {
        var sharedRequest = Guid.NewGuid();
        var durable = Result(sharedRequest, 0, "completed", 100);
        var accepted = Result(Guid.NewGuid(), 1, "accepted", 110);
        var newerDuplicate = durable with { UpdatedAtTicks = 120 };

        var merged = NCNeuralEmbodimentContextSystem.MergeRecentCommandResults(
            [durable],
            [accepted, newerDuplicate],
            [accepted]);

        Assert.Multiple(() =>
        {
            Assert.That(merged, Has.Length.EqualTo(2));
            Assert.That(merged[0], Is.EqualTo(accepted));
            Assert.That(merged[1], Is.EqualTo(newerDuplicate));
        });
    }

    private static NCNeuralCommandResult Result(Guid requestId, int commandIndex, string state, long ticks)
    {
        var failed = state is "failed" or "interrupted" or "skipped";
        return new NCNeuralCommandResult(
            "human-hold-position",
            state,
            failed ? "executor" : "none",
            failed ? "test-failure" : string.Empty,
            ticks)
        {
            RequestId = requestId,
            CapabilityId = "NCCognitionHoldPosition",
            CommandIndex = commandIndex,
            GoalKey = "execute",
            ExpectedOutcomeKey = "position-held",
        };
    }
}
