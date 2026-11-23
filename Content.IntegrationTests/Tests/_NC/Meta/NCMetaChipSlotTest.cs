// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Projects;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>A door has no editor of its own: it takes a program chip written at a terminal, or is driven by a server.</summary>
[TestFixture]
[TestOf(typeof(NCMetaWorkspaceSystem))]
public sealed class NCMetaChipSlotTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaWorkspaceSystem _workspace = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;

    private (EntityUid Door, MapCoordinates Where) Door()
    {
        Server.System<SharedMapSystem>().CreateMap(out var map);
        var where = new MapCoordinates(Vector2.Zero, map);
        return (SEntMan.SpawnEntity("NCMetaDoor", where), where);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ADoorHasNoEditorButTakesAChipAndNothingElse()
    {
        var (door, where) = Door();
        Assert.That(SEntMan.GetComponent<NCMetaTerminalComponent>(door).AllowEditor, Is.False);
        Assert.That(_workspace.CanOpenDevelopmentWorkspace(door), Is.False);

        var chip = SEntMan.SpawnEntity("NCMetaProgramChip", where);
        var crowbar = SEntMan.SpawnEntity("Crowbar", where);
        Assert.That(_slots.TryInsert(door, NCMetaChipSlot.Id, crowbar, null), Is.False, "Only META media fits the chip slot.");
        Assert.That(_slots.TryInsert(door, NCMetaChipSlot.Id, chip, null), Is.True);
        Assert.That(_slots.TryGetSlot(door, NCMetaChipSlot.Id, out var slot), Is.True);
        Assert.That(slot!.Item, Is.EqualTo(chip));

        Assert.That(_slots.TryEject(door, slot, null, out var ejected), Is.True);
        Assert.That(ejected, Is.EqualTo(chip));
        Assert.That(slot.Item, Is.Null);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ACartridgeFitsTheSlotToo()
    {
        var (door, where) = Door();
        var cartridge = SEntMan.SpawnEntity("NCMetaDataCartridge", where);
        Assert.That(_slots.TryInsert(door, NCMetaChipSlot.Id, cartridge, null), Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AChipHoldsLessThanACartridge()
    {
        var (_, where) = Door();
        var chip = SEntMan.SpawnEntity("NCMetaProgramChip", where);
        var cartridge = SEntMan.SpawnEntity("NCMetaDataCartridge", where);
        var small = SEntMan.GetComponent<NCMetaRemovableMediaComponent>(chip).CapacityBytes;
        var large = SEntMan.GetComponent<NCMetaRemovableMediaComponent>(cartridge).CapacityBytes;
        Assert.That(small, Is.LessThan(large));
    }
}
