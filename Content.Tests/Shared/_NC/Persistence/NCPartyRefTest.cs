// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Persistence;

[TestFixture]
[TestOf(typeof(NCPartyRef))]
public sealed class NCPartyRefTest
{
    [Test]
    public void CharacterPartyKeepsExistingProfileIdentity()
    {
        var characterId = new NCCharacterId(42);
        var party = NCPartyRef.ForCharacter(characterId);

        Assert.Multiple(() =>
        {
            Assert.That(party.IsValid, Is.True);
            Assert.That(party.Kind, Is.EqualTo(NCPartyKind.Character));
            Assert.That(party.TryGetCharacter(out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(characterId));
            Assert.That(party.TryGetActor(out _), Is.False);
            Assert.That(party.ToString(), Is.EqualTo("character:42"));
        });
    }

    [Test]
    public void ActorPartyDoesNotMasqueradeAsCharacter()
    {
        var actorId = new NCActorId(Guid.Parse("0d84bf0e-5c0e-46bf-9e45-f598ed27f932"));
        var party = NCPartyRef.ForActor(actorId);

        Assert.Multiple(() =>
        {
            Assert.That(party.IsValid, Is.True);
            Assert.That(party.Kind, Is.EqualTo(NCPartyKind.Actor));
            Assert.That(party.TryGetActor(out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(actorId));
            Assert.That(party.TryGetCharacter(out _), Is.False);
            Assert.That(party.ToString(), Is.EqualTo("actor:0d84bf0e-5c0e-46bf-9e45-f598ed27f932"));
        });
    }

    [Test]
    public void MixedOrEmptyIdentityIsInvalid()
    {
        var mixed = new NCPartyRef(NCPartyKind.Actor, new NCCharacterId(7), Guid.NewGuid());
        var emptyActor = new NCPartyRef(NCPartyKind.Actor, default, Guid.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(default(NCPartyRef).IsValid, Is.False);
            Assert.That(mixed.IsValid, Is.False);
            Assert.That(emptyActor.IsValid, Is.False);
        });
    }

    [Test]
    public void FactoriesRejectEmptyIdentity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => NCPartyRef.ForCharacter(default), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => NCPartyRef.ForActor(default), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => NCPartyRef.ForOrganization(default), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => NCPartyRef.ForBusiness(default), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => NCPartyRef.ForCityService(default), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => NCPartyRef.ForEstate(default), Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }
}
