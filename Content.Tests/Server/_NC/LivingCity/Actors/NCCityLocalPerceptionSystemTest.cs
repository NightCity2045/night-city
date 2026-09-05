// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Shared.GameObjects;

namespace Content.Tests.Server._NC.LivingCity.Actors;

[TestFixture]
public sealed class NCCityLocalPerceptionSystemTest
{
    [Test]
    public void RepeatedIncidentWakeIsBoundedButRecoversFromFutureTimestamp()
    {
        var interval = TimeSpan.FromSeconds(12);
        var previous = new NCActiveWorldTime(TimeSpan.FromSeconds(20).Ticks);

        Assert.That(NCCityLocalPerceptionSystem.ShouldRaiseRepeatedIncident(
            previous,
            new NCActiveWorldTime(TimeSpan.FromSeconds(31).Ticks),
            interval), Is.False);
        Assert.That(NCCityLocalPerceptionSystem.ShouldRaiseRepeatedIncident(
            previous,
            new NCActiveWorldTime(TimeSpan.FromSeconds(32).Ticks),
            interval), Is.True);
        Assert.That(NCCityLocalPerceptionSystem.ShouldRaiseRepeatedIncident(
            previous,
            new NCActiveWorldTime(TimeSpan.FromSeconds(10).Ticks),
            interval), Is.True);
    }

    [Test]
    public void UnseenIncidentSourceHandleCannotMasqueradeAsPerceivedPerson()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCCityLocalPerceptionSystem.IsUnidentifiedSourceHandle(
                "unidentified-source:0123456789abcdef"), Is.True);
            Assert.That(NCCityLocalPerceptionSystem.IsUnidentifiedSourceHandle("subject-17"), Is.False);
        });
    }

    [Test]
    public void OrdinarySnapshotChangesProduceTypedBoundedDeltas()
    {
        var previousAt = new NCActiveWorldTime(100);
        var now = new NCActiveWorldTime(200);
        var lifetime = TimeSpan.FromSeconds(30);
        var person = new EntityUid(1);
        var departingPerson = new EntityUid(2);
        var oldItem = new EntityUid(3);
        var newItem = new EntityUid(4);
        var output = new List<NCCityLocalPerceptionDelta>();
        NCCityLocalObservation[] previous =
        [
            Observation(person, "person-stays", NCCityLocalObservationKind.Neutral, previousAt),
            Observation(departingPerson, "person-leaves", NCCityLocalObservationKind.Neutral, previousAt),
            Observation(oldItem, "item-leaves", NCCityLocalObservationKind.Item, previousAt),
        ];
        NCCityLocalObservation[] current =
        [
            Observation(person, "person-stays", NCCityLocalObservationKind.Critical, now),
            Observation(newItem, "item-arrives", NCCityLocalObservationKind.Item, now),
        ];
        NCCityLocalHeldItemObservation[] oldHeld =
        [
            new(person, "person-stays", oldItem, "item-leaves"),
        ];
        NCCityLocalHeldItemObservation[] newHeld =
        [
            new(person, "person-stays", newItem, "item-arrives"),
        ];

        NCCityLocalPerceptionSystem.BuildPerceptionDeltas(
            previous, current, oldHeld, newHeld, now, lifetime, output);

        Assert.Multiple(() =>
        {
            Assert.That(output.Any(value =>
                value.Kind == NCCityLocalPerceptionDeltaKind.SubjectStateChanged &&
                value.SubjectHandle == "person-stays" && value.PreviousKind == "neutral" &&
                value.CurrentKind == "critical"), Is.True);
            Assert.That(output.Any(value =>
                value.Kind == NCCityLocalPerceptionDeltaKind.PersonLeft &&
                value.SubjectHandle == "person-leaves"), Is.True);
            Assert.That(output.Any(value =>
                value.Kind == NCCityLocalPerceptionDeltaKind.ItemDisappeared &&
                value.SubjectHandle == "item-leaves"), Is.True);
            Assert.That(output.Any(value =>
                value.Kind == NCCityLocalPerceptionDeltaKind.ItemAppeared &&
                value.SubjectHandle == "item-arrives"), Is.True);
            Assert.That(output.Any(value =>
                value.Kind == NCCityLocalPerceptionDeltaKind.HeldItemRemoved &&
                value.RelatedHandle == "item-leaves"), Is.True);
            Assert.That(output.Any(value =>
                value.Kind == NCCityLocalPerceptionDeltaKind.HeldItemAdded &&
                value.RelatedHandle == "item-arrives"), Is.True);
            Assert.That(output.All(value =>
                value.ObservedAt == now && value.ExpiresAt == now.Advance(lifetime)), Is.True);
        });
    }

    [Test]
    public void OrdinaryDeltaWakeCoalescesUntilCooldownExpires()
    {
        var now = new NCActiveWorldTime(100);
        var later = new NCActiveWorldTime(200);

        Assert.Multiple(() =>
        {
            Assert.That(NCCityLocalPerceptionSystem.ShouldRaisePerceptionDeltaWake(
                true, 3, now, now), Is.True);
            Assert.That(NCCityLocalPerceptionSystem.ShouldRaisePerceptionDeltaWake(
                true, 3, later, now), Is.False);
            Assert.That(NCCityLocalPerceptionSystem.ShouldRaisePerceptionDeltaWake(
                false, 3, now, now), Is.False);
            Assert.That(NCCityLocalPerceptionSystem.ShouldRaisePerceptionDeltaWake(
                true, 0, now, now), Is.False);
        });
    }

    private static NCCityLocalObservation Observation(
        EntityUid entity,
        string handle,
        NCCityLocalObservationKind kind,
        NCActiveWorldTime now) =>
        new(entity, handle, kind, 1f, 1f, now, now.Advance(TimeSpan.FromMinutes(1)));
}
