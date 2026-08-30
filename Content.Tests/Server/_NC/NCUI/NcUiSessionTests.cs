// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Server._NC.NCUI;
using Content.Shared._NC.NCUI;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.Tests.Server._NC.NCUI;

[TestFixture]
public sealed class NcUiSessionTests
{
    private enum TestUiKey : byte
    {
        Key,
    }

    [Test]
    public void SessionKeepsFrameworkStateSeparateFromDomainState()
    {
        var session = new NcUiSession(new NcUiSessionId(7), new EntityUid(1), TestUiKey.Key,
            NcUiSessionOptions.Default);
        var actor = new EntityUid(2);
        session.Participants.Add(actor, new NcUiServerParticipant(actor,
            NcUiPermission.View | NcUiPermission.Operate));
        session.Leases.Add(new NcUiObjectId(99), new NcUiServerLease(
            new NcUiObjectId(99), actor, new NcUiOperationId(4), TimeSpan.FromSeconds(2)));

        Assert.Multiple(() =>
        {
            Assert.That(session.Revision, Is.EqualTo(NcUiRevision.Initial));
            Assert.That(session.Participants[actor].Permissions.HasFlag(NcUiPermission.Operate), Is.True);
            Assert.That(session.Leases[new NcUiObjectId(99)].Owner, Is.EqualTo(actor));
        });
    }

    [Test]
    public void RatePoliciesAreSplitByTrafficClass()
    {
        var options = NcUiSessionOptions.Default;
        Assert.Multiple(() =>
        {
            Assert.That(options.GetRate(NcUiRateLimitChannel.Action), Is.EqualTo(options.ActionRate));
            Assert.That(options.GetRate(NcUiRateLimitChannel.Continuous), Is.EqualTo(options.ContinuousRate));
            Assert.That(options.GetRate(NcUiRateLimitChannel.Resync), Is.EqualTo(options.ResyncRate));
        });
    }

    [Test]
    public void DefaultOptionsPassProductionLimitValidation()
    {
        Assert.DoesNotThrow(() => NcUiSessionOptions.Default.Validate());
    }

    [Test]
    public void InvalidRatePolicyIsRejected()
    {
        var options = NcUiSessionOptions.Default with
        {
            ActionRate = new NcUiRateLimitPolicy(float.NaN, 1f),
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
