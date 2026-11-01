// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;
using Content.Shared.Interaction;
using Content.Shared.Power.Components;
using Content.Shared.PowerCell.Components;
using Content.Shared.Verbs;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    private void InitializePowerServiceEvents()
    {
        SubscribeLocalEvent<NCCyberwarePowerBusComponent, InteractUsingEvent>(OnPowerBusInteract);
        SubscribeLocalEvent<NCCyberwarePowerBusComponent, GetVerbsEvent<InteractionVerb>>(OnPowerBusVerbs);
        SubscribeLocalEvent<NCMedicalBodyComponent, GetVerbsEvent<InteractionVerb>>(OnBodyPowerVerbs);
    }

    /// <summary>Installed access is a real exposed thoracic site, including its own opened protective casing.</summary>
    public bool CanServicePowerBus(Entity<NCCyberwarePowerBusComponent?> bus, EntityUid user)
    {
        if (!Resolve(bus, ref bus.Comp, false) || TerminatingOrDeleted(bus) || !Exists(user) ||
            TerminatingOrDeleted(user))
            return false;
        var target = bus.Owner;
        if (TryResolveOwningBody(bus, out var body))
        {
            target = body;
            // A replacement organ may itself be the physical holder; no fictitious implant-slot edge is needed.
            if (TryComp<NCCyberwareComponent>(bus, out var direct) && direct.InstalledSlot == null &&
                TryComp<NCSurgicalSiteComponent>(bus, out var ownSite))
            {
                return ownSite.BoneCasingOpen && ownSite.TissuesRetracted &&
                       ownSite.AccessDepth is NCSurgicalAccessDepth.OrganExposed or NCSurgicalAccessDepth.ProcedureInProgress &&
                       _assemblyBlocker.CanInteract(user, body) &&
                       _assemblyInteraction.InRangeUnobstructed(user, body, 2f);
            }
            if (!TryComp<ChildOrganComponent>(bus, out var child) || child.Parent is not { } host ||
                !TryComp<NCSurgicalSiteComponent>(host, out var site) ||
                !TryComp<NCCyberwareComponent>(bus, out var module) || module.InstalledSlot is not { } slot ||
                !TryComp<NCMedicalBodyComponent>(body, out var medical) ||
                !_prototypes.TryIndex(medical.Profile, out var anatomy) ||
                !_prototypes.TryIndex(anatomy.SurgicalField, out var field) ||
                !field.CyberwareSlots.TryGetValue(slot, out var definition))
                return false;
            if (site.Incisions.Count > 0)
            {
                if (!site.Incisions.Any(incision => definition.Zones.Contains(incision.TargetZone) &&
                    !incision.Sutured && incision.BoneCasingOpen &&
                    incision.EdgeSeparation >= field.MinimumOpenRetraction && incision.Depth >= definition.MinimumDepth))
                    return false;
            }
            else if (!site.BoneCasingOpen || !site.TissuesRetracted || site.AccessDepth is not
                     (NCSurgicalAccessDepth.CavityOpened or NCSurgicalAccessDepth.EncasedStructureOpened or
                      NCSurgicalAccessDepth.OrganExposed or NCSurgicalAccessDepth.ProcedureInProgress or
                      NCSurgicalAccessDepth.ReadyForClosure))
                return false;
        }
        return _assemblyBlocker.CanInteract(user, target) &&
               _assemblyInteraction.InRangeUnobstructed(user, target, 2f);
    }

    public bool TryInsertPowerCell(Entity<NCCyberwarePowerBusComponent?> bus, EntityUid user, EntityUid cell, bool reserve)
    {
        if (!CanServicePowerBus(bus, user) || !_assemblyHands.TryGetActiveItem(user, out var held) || held != cell ||
            !HasComp<BatteryComponent>(cell) || !HasComp<PowerCellComponent>(cell) ||
            !_powerSlots.TryGetSlot(bus, reserve ? NCCyberwarePowerBusComponent.ReserveSlot :
                NCCyberwarePowerBusComponent.MainSlot, out var slot) || slot.Item != null)
            return false;
        if (TryResolveOwningBody(bus, out var body))
            TryAdvanceBodyPower(body, _timing.CurTime);
        _powerSlots.SetLock(bus, slot, false);
        try
        {
            if (!_powerSlots.TryInsertFromHand(bus, slot, user))
                return false;
            EnsureCellIdentity(cell);
            return slot.Item == cell;
        }
        finally
        {
            _powerSlots.SetLock(bus, slot, true);
        }
    }

    public bool TryEjectPowerCell(Entity<NCCyberwarePowerBusComponent?> bus, EntityUid user,
        EntityUid expectedCell, bool reserve)
    {
        if (!CanServicePowerBus(bus, user) || !_powerSlots.TryGetSlot(bus,
                reserve ? NCCyberwarePowerBusComponent.ReserveSlot : NCCyberwarePowerBusComponent.MainSlot,
                out var slot) || slot.Item != expectedCell)
            return false;
        if (TryResolveOwningBody(bus, out var body))
            TryAdvanceBodyPower(body, _timing.CurTime);
        _powerSlots.SetLock(bus, slot, false);
        try
        {
            return _powerSlots.TryEjectToHands(bus, slot, user);
        }
        finally
        {
            _powerSlots.SetLock(bus, slot, true);
        }
    }

    private void OnPowerBusInteract(Entity<NCCyberwarePowerBusComponent> bus, ref InteractUsingEvent args)
    {
        if (args.Handled || !HasComp<PowerCellComponent>(args.Used))
            return;
        args.Handled = TryInsertPowerCell(bus.AsNullable(), args.User, args.Used, false);
    }

    private void OnPowerBusVerbs(Entity<NCCyberwarePowerBusComponent> bus, ref GetVerbsEvent<InteractionVerb> args)
    {
        AddPowerCellVerbs(bus.AsNullable(), ref args);
    }

    private void OnBodyPowerVerbs(Entity<NCMedicalBodyComponent> body, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        foreach (var uid in CollectCyberware(body))
        {
            if (TryComp<NCCyberwarePowerBusComponent>(uid, out var bus))
                AddPowerCellVerbs((uid, bus), ref args);
        }
    }

    private void AddPowerCellVerbs(Entity<NCCyberwarePowerBusComponent?> bus, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!CanServicePowerBus(bus, args.User))
            return;
        var user = args.User;
        foreach (var reserve in new[] { false, true })
        {
            var capturedReserve = reserve;
            if (!_powerSlots.TryGetSlot(bus, reserve ? NCCyberwarePowerBusComponent.ReserveSlot :
                    NCCyberwarePowerBusComponent.MainSlot, out var slot))
                continue;
            if (slot.Item is { } cell)
            {
                args.Verbs.Add(new InteractionVerb
                {
                    Text = Loc.GetString(reserve ? "nc-cyberware-power-eject-reserve" : "nc-cyberware-power-eject-main"),
                    Act = () => TryEjectPowerCell(bus, user, cell, capturedReserve),
                });
            }
            else if (_assemblyHands.TryGetActiveItem(user, out var held) && held is { } battery &&
                     HasComp<PowerCellComponent>(battery))
            {
                args.Verbs.Add(new InteractionVerb
                {
                    Text = Loc.GetString(reserve ? "nc-cyberware-power-insert-reserve" : "nc-cyberware-power-insert-main"),
                    Act = () => TryInsertPowerCell(bus, user, battery, capturedReserve),
                });
            }
        }
    }
}
