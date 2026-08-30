// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Social;

[TestFixture]
public sealed class NCConversationArchiveSystemTest
{
    [Test]
    public void SummaryKeepsImportantSpeechActsAndRecentContext()
    {
        var actor = NCActorId.New();
        var self = NCPartyRef.ForActor(actor);
        var player = NCPartyRef.ForCharacter(new NCCharacterId(1));
        NCConversationTranscriptTurn[] transcript =
        [
            new(player, NCSpeechActKind.Statement, "Old small talk", new NCActiveWorldTime(1)),
            new(player, NCSpeechActKind.Threat, "Give me the money or I shoot", new NCActiveWorldTime(2)),
            new(self, NCSpeechActKind.Statement, "Unrelated middle turn", new NCActiveWorldTime(3)),
            new(self, NCSpeechActKind.Question, "What do you want?", new NCActiveWorldTime(4)),
        ];

        var summary = NCConversationArchiveSystem.BuildSummary(actor, transcript, ["debt"], 2, 512);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("interlocutor[threat]: Give me the money or I shoot"));
            Assert.That(summary, Does.Contain("self[question]: What do you want?"));
            Assert.That(summary, Does.Not.Contain("Old small talk"));
            Assert.That(summary, Does.Not.Contain("Unrelated middle turn"));
        });
    }

    [Test]
    public void SummaryRespectsConfiguredLength()
    {
        var actor = NCActorId.New();
        var transcript = new[]
        {
            new NCConversationTranscriptTurn(
                NCPartyRef.ForCharacter(new NCCharacterId(1)),
                NCSpeechActKind.Statement,
                new string('x', 128),
                new NCActiveWorldTime(1)),
        };

        var summary = NCConversationArchiveSystem.BuildSummary(actor, transcript, [], 1, 32);

        Assert.That(summary, Has.Length.EqualTo(32));
    }
}
