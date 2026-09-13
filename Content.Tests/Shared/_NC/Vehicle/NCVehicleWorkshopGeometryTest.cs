// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared._NC.Vehicle.Workshop;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Vehicle;

#pragma warning disable RA0039 // Deliberately malformed data exercises validation without loading a server.
[TestFixture]
public sealed class NCVehicleWorkshopGeometryTest
{
    [Test]
    public void TransparentPixelsAndNonFiniteCoordinatesCannotSelectAHiddenPart()
    {
        var node = new NCVehicleWorkshopNode { Position = new(.2f, .2f), Size = new(.6f, .6f), HitMask = ["10", "01"] };
        Assert.That(NCVehicleWorkshopGeometry.Contains(node, new(.3f,.3f)), Is.True);
        Assert.That(NCVehicleWorkshopGeometry.Contains(node, new(.7f,.3f)), Is.False);
        Assert.That(NCVehicleWorkshopGeometry.Contains(node, new(.9f,.9f)), Is.False);
        Assert.That(NCVehicleWorkshopGeometry.Contains(node, new(float.NaN,.3f)), Is.False);
        Assert.That(NCVehicleWorkshopGeometry.Contains(node, new(.3f,float.PositiveInfinity)), Is.False);
    }

    [Test]
    public void AccessAndCarrierCyclesAreRejectedBeforeTheyCanStrandParts()
    {
        var chassis = new NCVehicleChassisPrototype { Mass = 1, Inertia = 1 };
        chassis.Slots["a"] = new() { Mount = "a", BlockedBy = ["b"] };
        chassis.Slots["b"] = new() { Mount = "b", BlockedBy = ["a"] };
        var profile = new NCVehicleWorkshopPrototype();
        profile.Views["front"] = new();
        profile.Nodes["a"] = new() { Slot = "a", View = "front", Size = Vector2.One };
        Assert.That(NCVehicleWorkshopGeometry.Valid(profile, chassis), Is.False);
        chassis.Slots["b"].BlockedBy.Clear();
        Assert.That(NCVehicleWorkshopGeometry.Valid(profile, chassis), Is.True);
        chassis.Slots["a"].ExtractWith.Add("b");
        chassis.Slots["b"].ExtractWith.Add("a");
        Assert.That(NCVehicleWorkshopGeometry.Valid(profile, chassis), Is.False);
    }

    [Test]
    public void ModelLayoutUsesTheSameCoordinatesForPickingAndWorkPoints()
    {
        var profile = new NCVehicleWorkshopPrototype();
        profile.ViewLayouts["engine-bay"] = new()
        {
            Scale = new(.5f, .75f),
            Offset = new(.2f, .1f),
            AccessPosition = new(0, 2),
        };
        profile.NodeLayouts["engine"] = new() { Offset = new(.1f, 0), Scale = new(.5f, 1) };
        var node = new NCVehicleWorkshopNode
        {
            View = "engine-bay",
            Position = new(.2f, .2f),
            Size = new(.4f, .4f),
        };

        var layout = NCVehicleWorkshopGeometry.Layout(profile, "engine", node);
        Assert.That(layout.Position, Is.EqualTo(new Vector2(.4f, .25f)));
        Assert.That(layout.Size, Is.EqualTo(new Vector2(.1f, .3f)));
        Assert.That(NCVehicleWorkshopGeometry.Contains(profile, "engine", node, new(.45f, .4f)), Is.True);
        Assert.That(NCVehicleWorkshopGeometry.LocalPoint(profile, "engine", node, new(.45f, .4f)),
            Is.EqualTo(new Vector2(.5f, .5f)).Using<Vector2>((a, b) => (a - b).Length() < .0001f));
        Assert.That(NCVehicleWorkshopGeometry.AccessPosition(profile, "engine-bay", new()),
            Is.EqualTo(new Vector2(0, 2)));
    }
}
