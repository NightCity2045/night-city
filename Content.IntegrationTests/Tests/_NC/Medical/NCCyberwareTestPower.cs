// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Cyberware;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Medical;

internal static class NCCyberwareTestPower
{
    public static EntityUid Install(IEntityManager entities, NCCyberwareSystem cyberware,
        ItemSlotsSystem slots, SharedBatterySystem batteries, EntityUid body, float charge = 10000f)
    {
        var thorax = entities.GetComponent<BodyComponent>(body).Organs!.ContainedEntities.Single(uid =>
            entities.GetComponent<OrganComponent>(uid).Category == "Thorax");
        var coordinates = entities.GetComponent<TransformComponent>(body).Coordinates;
        var holder = entities.SpawnEntity("NCImplantPowerBusMk1", coordinates);
        Assert.That(cyberware.TryInstall(new NCCyberwareInstallRequest(NCActionResolutionId.New(), body,
            thorax, holder, "NCSlotThoracicPower")).Accepted, Is.True);
        var cell = entities.SpawnEntity("NCCyberwarePowerCell", coordinates);
        batteries.SetCharge(cell, charge);
        Insert(slots, holder, cell, false);
        cyberware.ResetPowerClock(body);
        return cell;
    }

    public static void Insert(ItemSlotsSystem slots, EntityUid holder, EntityUid cell, bool reserve)
    {
        var id = reserve ? NCCyberwarePowerBusComponent.ReserveSlot : NCCyberwarePowerBusComponent.MainSlot;
        slots.SetLock(holder, id, false);
        try
        {
            Assert.That(slots.TryInsert(holder, id, cell, null), Is.True);
        }
        finally
        {
            slots.SetLock(holder, id, true);
        }
    }
}
