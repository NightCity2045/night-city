// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Server._NC.LivingCity.Cognition;
using Content.Shared.DoAfter;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using NUnit.Framework;

namespace Content.Tests.Server._NC.LivingCity.Cognition;


#pragma warning disable RA0039 // Unit test instantiates test prototypes directly
[TestFixture]
public sealed class NCHumanVanillaActionAdapterTest
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
}
