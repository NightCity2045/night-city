// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;

namespace Content.Server._NC.Meta.Devices;

public sealed partial class NCMetaDoorPanelSystem
{
    private sealed class PanelDeferredAuthority(NCMetaDoorPanelSystem system, EntityUid panel, EntityUid door,
        EntityUid actor, string panelId, string doorId, ulong panelEpoch, ulong doorEpoch,
        ulong panelAuthority, ulong doorAuthority, TimeSpan deadline) : INCMetaDeferredInvocationAuthority
    {
        public bool IsCurrent() => system._timing.CurTime < deadline && system.Exists(actor) &&
            system._devices.TryGetEntry(panel, out var source) && source.Id == panelId &&
            source.DeviceEpoch == panelEpoch && source.AuthorityEpoch == panelAuthority &&
            system.TryComp<NCMetaDoorPanelComponent>(panel, out var configuration) && configuration.DoorId == doorId &&
            system._devices.TryGetEntry(door, out var target) && target.Id == doorId &&
            target.DeviceEpoch == doorEpoch && target.AuthorityEpoch == doorAuthority &&
            system._devices.IsPowered(panel) && system._runtime.CanReach(panel, door) &&
            system._interaction.InRangeUnobstructed(actor, panel, range: 2) && system._doors.HasAccess(door, actor);
    }
}
