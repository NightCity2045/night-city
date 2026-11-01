// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Surgery;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
public sealed class NCFreeformSurgerySessionTest : GameTest
{
    [Test, RunOnSide(Side.Server)]
    public void ReopeningTheSamePatientPreservesPendingActionIdentity()
    {
        var maps = SEntMan.System<SharedMapSystem>();
        var surgery = SEntMan.System<NCFreeformSurgerySystem>();
        maps.CreateMap(out var map);
        var coordinates = new MapCoordinates(Vector2.Zero, map);
        var surgeon = SEntMan.SpawnEntity("MobHuman", coordinates);
        var patient = SEntMan.SpawnEntity("MobHuman", coordinates);
        var otherPatient = SEntMan.SpawnEntity("MobHuman", coordinates);
        var player = Server.PlayerMan.Sessions.Single();
        var previous = player.AttachedEntity;
        try
        {
            Server.PlayerMan.SetAttachedEntity(player, surgeon);
            Assert.That(surgery.TryOpenSession(surgeon, patient), Is.True);
            var session = SEntMan.GetComponent<NCFreeformSurgerySessionComponent>(surgeon);
            var identity = session.SessionId;
            session.Depth = 0.25f;
            session.LastSequence = 4;

            Assert.That(surgery.TryOpenSession(surgeon, patient), Is.True);
            Assert.That(session.SessionId, Is.EqualTo(identity));
            Assert.That(session.Depth, Is.EqualTo(0.25f));
            Assert.That(session.LastSequence, Is.EqualTo(4));

            Assert.That(surgery.TryOpenSession(surgeon, otherPatient), Is.True);
            Assert.That(session.SessionId, Is.Not.EqualTo(identity));
            Assert.That(session.Patient, Is.EqualTo(otherPatient));
        }
        finally
        {
            Server.PlayerMan.SetAttachedEntity(player, previous);
            maps.DeleteMap(map);
        }
    }
}
