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
}
