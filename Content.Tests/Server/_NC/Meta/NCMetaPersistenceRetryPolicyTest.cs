// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Meta;

[TestFixture]
public sealed class NCMetaPersistenceRetryPolicyTest
{
    [TestCase("40001", true)]
    [TestCase("40P01", true)]
    [TestCase("23503", false)]
    [TestCase("23505", false)]
    public void ProviderSerializationWrappersRetryButConstraintFailuresDoNot(string state, bool transient)
    {
        var provider = new PostgresException("isolated test", "ERROR", "ERROR", state);
        Assert.That(ServerDbManager.IsTransient(provider), Is.EqualTo(transient));
        Assert.That(ServerDbManager.IsTransient(new DbUpdateException("save", provider)), Is.EqualTo(transient));
        Assert.That(ServerDbManager.IsTransient(new InvalidOperationException("execute update", provider)), Is.EqualTo(transient));
        Assert.That(ServerDbManager.IsTransient(new InvalidOperationException("business refusal")), Is.False);
    }
}
