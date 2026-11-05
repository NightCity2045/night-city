// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Numerics;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Optics;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCOpticTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [Test]
    public void ShieldBlocksOnlyTheSegmentBetweenSensorAndContact()
    {
        Assert.That(NCOpticMath.SegmentIntersectsShield(Vector2.Zero, new(6, 0), new(3, .2f), .75f), Is.True);
        Assert.That(NCOpticMath.SegmentIntersectsShield(Vector2.Zero, new(6, 0), new(8, 0), .75f), Is.False);
        Assert.That(NCOpticMath.SegmentIntersectsShield(Vector2.Zero, new(6, 0), new(3, 2), .75f), Is.False);
        Assert.That(NCOpticMath.SegmentIntersectsShield(Vector2.Zero, Vector2.Zero, Vector2.Zero, .75f), Is.True);
    }

    [Test]
    public void StationaryContactsDoNotBecomeMovementAndPrecisionIsQuantized()
    {
        Assert.That(NCOpticMath.DetectMovement(new(1, 1), new(1, 1), 1), Is.False);
        Assert.That(NCOpticMath.DetectMovement(new(1, 1), new(1.5f, 1), 1), Is.True);
        Assert.That(NCOpticMath.DetectMovement(Vector2.Zero, new(float.NaN, 0), 1), Is.False);
        Assert.That(NCOpticMath.Quantize(new(1.24f, -1.24f), .5f), Is.EqualTo(new Vector2(1, -1)));
    }

    [Test]
    public void PhotoMemoryAndRadarCooldownSurviveMedicalSerialization()
    {
        var pixels = new byte[NCOpticMemoryComponent.Bytes]; pixels[0] = 240; pixels[^1] = 27;
        var partId = Guid.NewGuid();
        var state = new NCCyberwareStateSnapshot { Profile = "NCCyberEyeLeftProfile", OpticRadarCooldownSeconds = 12,
            Assembly = new() { Parts = [new() { Slot = "NCOpticModule1Slot", StableInstanceId = partId,
                EntityPrototype = "NCOpticRecordingStandard", OpticPhotos = [pixels] }] } };
        var serializer = IoCManager.Resolve<ISerializationManager>();
        var copy = serializer.Read<NCCyberwareStateSnapshot>(serializer.WriteValue(state, alwaysWrite: true), notNullableOverride: true);
        Assert.That(copy.OpticRadarCooldownSeconds, Is.EqualTo(12));
        Assert.That(copy.Assembly!.Parts[0].StableInstanceId, Is.EqualTo(partId));
        Assert.That(copy.Assembly.Parts[0].OpticPhotos[0], Is.EqualTo(pixels));
        copy.Assembly.Parts[0].OpticPhotos[0][0] = 0;
        Assert.That(pixels[0], Is.EqualTo(240));
    }
}
