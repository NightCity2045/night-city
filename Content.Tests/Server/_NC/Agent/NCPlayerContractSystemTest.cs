// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Server._NC.Agent;
using Content.Shared._NC.Agent.UI;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Agent;

[TestFixture]
public sealed class NCPlayerContractSystemTest
{
    [TestCase(NCPlayerContractStatus.Accepted)]
    [TestCase(NCPlayerContractStatus.Ready)]
    public void OnlyCustomerConfirmsCompletion(NCPlayerContractStatus status)
    {
        Assert.That(NCPlayerContractSystem.CanChangeStatus(status, 1, 2, 1,
            NCPlayerContractAction.Complete, out var next), Is.True);
        Assert.That(next, Is.EqualTo(NCPlayerContractStatus.Completed));
        foreach (var actor in new[] { 2, 3 })
            Assert.That(NCPlayerContractSystem.CanChangeStatus(status, 1, 2, actor,
                NCPlayerContractAction.Complete, out _), Is.False);
    }

    [TestCase(NCPlayerContractStatus.Completed)]
    [TestCase(NCPlayerContractStatus.Cancelled)]
    [TestCase(NCPlayerContractStatus.Withdrawn)]
    [TestCase(NCPlayerContractStatus.Expired)]
    public void TerminalHistoryCannotBeReopenedOrCompletedAgain(NCPlayerContractStatus status)
    {
        foreach (var action in Enum.GetValues<NCPlayerContractAction>())
        foreach (var actor in new[] { 1, 2, 3 })
            Assert.That(NCPlayerContractSystem.CanChangeStatus(status, 1, 2, actor, action, out _), Is.False);
    }

    [Test]
    public void TakingOwnContractAndTakingAssignedContractAreRejected()
    {
        Assert.That(NCPlayerContractSystem.CanChangeStatus(NCPlayerContractStatus.Open, 1, null, 1,
            NCPlayerContractAction.Accept, out _), Is.False);
        Assert.That(NCPlayerContractSystem.CanChangeStatus(NCPlayerContractStatus.Open, 1, null, 2,
            NCPlayerContractAction.Accept, out var accepted), Is.True);
        Assert.That(NCPlayerContractSystem.CanChangeStatus(accepted, 1, 2, 3,
            NCPlayerContractAction.Accept, out _), Is.False);
    }

    [Test]
    public void CustomerCannotClaimContractorReadinessOrWithdrawal()
    {
        foreach (var action in new[] { NCPlayerContractAction.Ready, NCPlayerContractAction.Withdraw })
        {
            Assert.That(NCPlayerContractSystem.CanChangeStatus(NCPlayerContractStatus.Accepted, 1, 2, 1, action, out _), Is.False);
            Assert.That(NCPlayerContractSystem.CanChangeStatus(NCPlayerContractStatus.Accepted, 1, 2, 2, action, out _), Is.True);
        }
    }
}
