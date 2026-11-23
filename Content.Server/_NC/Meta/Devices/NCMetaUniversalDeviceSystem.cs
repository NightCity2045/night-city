// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.Server.Power.Components;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared._NC.Meta;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Power.Components;
using Content.Shared.PowerCell.Components;
using Robust.Shared.GameObjects;

namespace Content.Server._NC.Meta.Devices;

/// <summary>
/// Every electronic device carries a META ID: anything that draws or stores power, or has a network address, is
/// registered the moment it starts. The ID is an address only; it grants nothing and the device is not claimable.
/// A device placed on the map keeps the same ID between restarts, derived from what it is and where it stands.
/// </summary>
public sealed class NCMetaUniversalDeviceSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        // The engine allows one handler per component and event, and power, batteries and networks already have theirs,
        // so the system listens to every entity finishing its start instead and looks at what it carries.
        EntityManager.EntityInitialized += OnEntityInitialized;
    }

    public override void Shutdown()
    {
        EntityManager.EntityInitialized -= OnEntityInitialized;
        base.Shutdown();
    }

    private void OnEntityInitialized(Entity<MetaDataComponent> entity)
    {
        var uid = entity.Owner;
        if (HasComp<ApcPowerReceiverComponent>(uid) || HasComp<PowerSupplierComponent>(uid) || HasComp<BatteryComponent>(uid) ||
            HasComp<PowerCellSlotComponent>(uid) || HasComp<DeviceNetworkComponent>(uid))
            Attach(uid);
    }

    private void Attach(EntityUid uid)
    {
        if (HasComp<NCMetaDeviceComponent>(uid) || HasComp<NCMetaHostComponent>(uid) || TerminatingOrDeleted(uid))
            return;

        var device = new NCMetaDeviceComponent { Automatic = true };
        // A persistent item derives its ID from its item identity; everything else from its place in the world.
        if (!HasComp<NCCityPersistentItemAdapterComponent>(uid))
            device.DeviceId = NCMetaDeviceIds.FromKey(PlaceKey(uid));
        AddComp(uid, device);
    }

    private string PlaceKey(EntityUid uid)
    {
        var meta = MetaData(uid);
        var transform = Transform(uid);
        var prototype = meta.EntityPrototype?.ID ?? "none";
        var parentName = "";
        var parentPosition = Vector2.Zero;
        if (transform.ParentUid.IsValid())
        {
            parentName = MetaData(transform.ParentUid).EntityName;
            parentPosition = Transform(transform.ParentUid).LocalPosition;
        }

        var position = transform.LocalPosition;
        return string.Join(':', "meta-device:place", prototype, parentName,
            (int) MathF.Round(parentPosition.X * 8), (int) MathF.Round(parentPosition.Y * 8),
            (int) MathF.Round(position.X * 16), (int) MathF.Round(position.Y * 16),
            (int) MathF.Round((float) transform.LocalRotation.Degrees));
    }
}
