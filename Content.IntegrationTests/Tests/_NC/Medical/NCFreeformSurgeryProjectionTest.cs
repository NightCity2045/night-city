// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System;
using System.Numerics;
using System.Reflection;
using Content.Client._NC.Medical.Surgery;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
public sealed class NCFreeformSurgeryProjectionTest : GameTest
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly ProtoId<NCAnatomyProfilePrototype> Human = "Human";
    private static readonly ProtoId<NCTargetZonePrototype> Chest = "NCZoneChest";
    private static readonly ProtoId<NCTargetZonePrototype> LeftArm = "NCZoneArmLeft";
    private static readonly ProtoId<NCTargetZonePrototype> RightArm = "NCZoneArmRight";

    public override PoolSettings PoolSettings => new() { Connected = false };

    [TestCase(NCWoundSurface.Front)]
    [TestCase(NCWoundSurface.Rear)]
    [TestCase(NCWoundSurface.Left)]
    [TestCase(NCWoundSurface.Right)]
    [RunOnSide(Side.Client)]
    public void HitTestingAndIncisionsUseTheSameFixedAnatomicalSide(NCWoundSurface surface)
    {
        var field = CreateField(State(surface));
        try
        {
            var body = Body(field);
            foreach (var (zone, canvasPoint) in new[]
                     {
                         (RightArm, new Vector2(35f, 55f)),
                         (LeftArm, new Vector2(92f, 55f)),
                     })
            {
                var point = canvasPoint / new Vector2(128f, 192f) * (Vector2) body.PixelSize;
                AssertHit(body, point, zone, true);

                var incision = new NCFreeformSurgeryIncisionSummary(Guid.NewGuid(), zone, surface,
                    [], 0.2f, 0.01f, 0f, 0f, false, false, default, default);
                object?[] args = [incision, default(UIBox2)];
                Assert.That((bool) body.GetType().GetMethod("TryClosedSiteRect", Private)!.Invoke(body, args)!, Is.True);
                var bounds = (UIBox2) args[1]!;
                Assert.That(bounds.Center.X > body.PixelWidth * 0.5f, Is.EqualTo(zone == LeftArm));
                field.SetState(State(surface, incisions: [incision]), 0f);
                object?[] hitArgs = [bounds.Center, default(ProtoId<NCTargetZonePrototype>),
                    Vector2.Zero, default(NCFreeformSurgeryIncisionSummary)];
                Assert.That((bool) body.GetType().GetMethod("TryResolveIncisionAtScreen", Private)!
                    .Invoke(body, hitArgs)!, Is.True);
                Assert.That((ProtoId<NCTargetZonePrototype>) hitArgs[1]!, Is.EqualTo(zone));
                field.SetState(State(surface), 0f);
            }
        }
        finally
        {
            field.Orphan();
        }
    }

    [TestCase(NCWoundSurface.Front)]
    [TestCase(NCWoundSurface.Rear)]
    [TestCase(NCWoundSurface.Left)]
    [TestCase(NCWoundSurface.Right)]
    [RunOnSide(Side.Client)]
    public void OnlyAServerStagedMissingZoneHasAReceivingSiteTarget(NCWoundSurface surface)
    {
        var field = CreateField(State(surface, missing: true));
        try
        {
            var body = Body(field);
            var anchor = new Vector2(92f, 60.5f) / new Vector2(128f, 192f) * (Vector2) body.PixelSize;
            AssertHit(body, anchor, LeftArm, false);

            field.SetState(State(surface, missing: true, staged: true), 0f);
            AssertHit(body, anchor, LeftArm, true);

            // The marker exposes only the receiving site, not the entire absent arm.
            var distal = new Vector2(92f, 78f) / new Vector2(128f, 192f) * (Vector2) body.PixelSize;
            AssertHit(body, distal, LeftArm, false);

            field.SetState(State(surface, missing: true), 0f);
            AssertHit(body, anchor, LeftArm, false);
        }
        finally
        {
            field.Orphan();
        }
    }

    private static NCFreeformSurgeryFieldControl CreateField(NCFreeformSurgeryState state)
    {
        var field = new NCFreeformSurgeryFieldControl();
        field.SetState(state, 0f);
        field.Measure(new Vector2(600f, 600f));
        field.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(600f, 600f)));
        return field;
    }

    private static Control Body(NCFreeformSurgeryFieldControl field) =>
        (Control) typeof(NCFreeformSurgeryFieldControl).GetField("_body", Private)!.GetValue(field)!;

    private static void AssertHit(Control body, Vector2 point, ProtoId<NCTargetZonePrototype> expected, bool hit)
    {
        object?[] args = [point, default(ProtoId<NCTargetZonePrototype>), Vector2.Zero];
        Assert.That((bool) body.GetType().GetMethod("TryResolveSkinPoint", Private)!.Invoke(body, args)!, Is.EqualTo(hit));
        if (hit)
            Assert.That((ProtoId<NCTargetZonePrototype>) args[1]!, Is.EqualTo(expected));
    }

    private static NCFreeformSurgeryState State(NCWoundSurface surface, bool missing = false, bool staged = false,
        NCFreeformSurgeryIncisionSummary[]? incisions = null) => new(
        sessionId: NCActionResolutionId.New(), revision: 0, lastSequence: 0, accepted: true,
        failure: NCFreeformSurgeryFailure.None, anatomyProfile: Human, patient: default,
        selectedZone: Chest, activeIncisionId: null, surface: surface,
        missingZones: missing ? [LeftArm] : [], patientName: "Patient", toolName: "", toolPrototype: "",
        toolSterility: 1f, condition: default, consciousness: default, oxygenation: 1f, perfusion: 1f,
        pain: 0f, shock: 0f, movement: 0f, selectedDepth: 0f, siteContamination: 0f,
        accessDepth: default, capabilities: [], layers: [], structures: [], incisions: incisions ?? [], wounds: [], attachments: [],
        stagedGraftZone: staged ? (ProtoId<NCTargetZonePrototype>?) LeftArm : null);
}
