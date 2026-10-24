// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwareServiceTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [Test]
    public void TransferBetweenCharactersKeepsIdentityDefectsAndPrivateHistory()
    {
        var state = new NCCyberwareStateSnapshot
        {
            Profile = "NCSandevistan", StableInstanceId = Guid.NewGuid(), PermanentDamage = .3f,
            Heat = 17f, AbilityCooldownSeconds = 23f, Service = new NCCyberwareServiceState(),
        };
        Assert.That(NCCyberwareServiceHistory.TryAppend(state.Service, 0, Guid.NewGuid(), NCCyberwareServiceOperation.Removed, "1:7"), Is.True);
        Assert.That(NCCyberwareServiceHistory.TryAppend(state.Service, 1, Guid.NewGuid(), NCCyberwareServiceOperation.Installed, "1:9"), Is.True);
        state.Service.Firmware = "NCFirmwareServiceLock";
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var copy = serialization.Read<NCCyberwareStateSnapshot>(serialization.WriteValue(state, alwaysWrite: true), notNullableOverride: true);
        Assert.Multiple(() =>
        {
            Assert.That(copy.StableInstanceId, Is.EqualTo(state.StableInstanceId));
            Assert.That(copy.PermanentDamage, Is.EqualTo(.3f));
            Assert.That(copy.Heat, Is.EqualTo(17f));
            Assert.That(copy.AbilityCooldownSeconds, Is.EqualTo(23f));
            Assert.That(copy.Service!.Firmware.Id, Is.EqualTo("NCFirmwareServiceLock"));
            Assert.That(copy.Service.History.ConvertAll(entry => entry.Owner), Is.EqualTo(new[] { "1:7", "1:9" }));
        });
        copy.Service!.History[0].Owner = "forged";
        Assert.That(state.Service.History[0].Owner, Is.EqualTo("1:7"));
    }

    [Test]
    public void ReplayedOrStaleServiceCommandCannotAdvanceHistory()
    {
        var state = new NCCyberwareServiceState();
        var resolution = Guid.NewGuid();
        Assert.That(NCCyberwareServiceHistory.TryAppend(state, 0, resolution, NCCyberwareServiceOperation.Firmware, ""), Is.True);
        var saved = NCCyberwareServiceHistory.Clone(state);
        Assert.That(NCCyberwareServiceHistory.TryAppend(saved, 1, resolution, NCCyberwareServiceOperation.Firmware, ""), Is.False);
        Assert.That(NCCyberwareServiceHistory.TryAppend(saved, 0, Guid.NewGuid(), NCCyberwareServiceOperation.Firmware, ""), Is.False);
        Assert.That(saved.Revision, Is.EqualTo(1));
        Assert.That(saved.History, Has.Count.EqualTo(1));
    }

    [Test]
    public void BoundedHistoryRetainsMonotonicRevisionAndEvidenceOfOlderEvents()
    {
        var state = new NCCyberwareServiceState();
        for (uint i = 0; i < 1000; i++)
            Assert.That(NCCyberwareServiceHistory.TryAppend(state, i, Guid.NewGuid(), NCCyberwareServiceOperation.Tested, ""), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(NCCyberwareServiceHistory.Valid(state), Is.True);
            Assert.That(state.History, Has.Count.EqualTo(64));
            Assert.That(state.OmittedEvents, Is.EqualTo(936));
            Assert.That(state.History[0].Revision, Is.EqualTo(937));
            Assert.That(state.History[^1].Revision, Is.EqualTo(1000));
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void CorruptPersistedHistoryIsRejected(int corruption)
    {
        var state = new NCCyberwareServiceState();
        NCCyberwareServiceHistory.TryAppend(state, 0, Guid.NewGuid(), NCCyberwareServiceOperation.Installed, "1:3");
        switch (corruption)
        {
            case 0: state.History[0].ResolutionId = Guid.Empty; break;
            case 1: state.History[0].Revision = 2; break;
            case 2: state.History[0].Owner = new string('x', 161); break;
            case 3: state.OmittedEvents = 1; break;
        }
        Assert.That(NCCyberwareServiceHistory.Valid(state), Is.False);
    }

    [TestCase(10000, 10007, 2, 9)]
    [TestCase(10000, 9999, 2, 2)]
    [TestCase(0, 7, 90000, 90007)]
    public void WorldRestartFreezesRemainingEmpTime(double captured, double deadline, double runtime, double expected)
    {
        Assert.That(NCCyberwarePersistenceClock.TryRebaseDeadline(TimeSpan.FromSeconds(deadline),
            TimeSpan.FromSeconds(captured), TimeSpan.FromSeconds(runtime), out var rebased), Is.True);
        Assert.That(rebased, Is.EqualTo(TimeSpan.FromSeconds(expected)));
    }

    [Test]
    public void InvalidEpochAndOverflowCannotReachRuntime()
    {
        Assert.That(NCCyberwarePersistenceClock.TryRebaseDeadline(TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.Zero, out _), Is.False);
        Assert.That(NCCyberwarePersistenceClock.TryRebaseDeadline(TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromSeconds(1), out _), Is.False);
    }
}
