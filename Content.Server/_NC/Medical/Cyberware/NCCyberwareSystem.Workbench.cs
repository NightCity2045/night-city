// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private SharedHandsSystem _assemblyHands = default!;
    [Dependency] private SharedInteractionSystem _assemblyInteraction = default!;
    [Dependency] private ActionBlockerSystem _assemblyBlocker = default!;
    [Dependency] private SharedStackSystem _assemblyStacks = default!;
    [Dependency] private SharedPopupSystem _assemblyPopup = default!;

    private void InitializeWorkbenchEvents()
    {
        SubscribeLocalEvent<NCCyberwareWorkbenchComponent, ComponentInit>(OnWorkbenchInit);
        SubscribeLocalEvent<NCCyberwareWorkbenchComponent, InteractUsingEvent>(OnWorkbenchInteract);
        SubscribeLocalEvent<NCCyberwareWorkbenchComponent, GetVerbsEvent<InteractionVerb>>(OnWorkbenchVerbs);
        SubscribeLocalEvent<NCCyberwareWorkbenchComponent, ExaminedEvent>(OnWorkbenchExamined);
    }

    private void OnWorkbenchInit(Entity<NCCyberwareWorkbenchComponent> bench, ref ComponentInit args)
    {
        bench.Comp.Workpiece = _containers.EnsureContainer<ContainerSlot>(bench,
            NCCyberwareWorkbenchComponent.ContainerId);
    }

    private void OnWorkbenchInteract(Entity<NCCyberwareWorkbenchComponent> bench, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;
        // The native ItemSlots handler owns cell insertion. Service routing must leave that interaction available.
        if (HasComp<Content.Shared.PowerCell.Components.PowerCellComponent>(args.Used))
            return;
        if (HasComp<Content.Shared.Paper.PaperComponent>(args.Used))
        {
            args.Handled = TryWriteServiceDocument(bench.AsNullable(), args.User, args.Used);
            return;
        }
        if (HasComp<NCCyberwareFirmwareCardComponent>(args.Used))
        {
            args.Handled = true;
            if (TryGetServiceWorkpiece(bench.AsNullable(), out var workpiece) &&
                TryComp<NCCyberwareComponent>(workpiece, out var hardware))
                TryFlashFirmware(bench.AsNullable(), args.User, args.Used, workpiece, hardware.Service.Revision);
            return;
        }
        var accepted = HasComp<NCCyberwareComponent>(args.Used)
            ? TryMountAssembly(bench.AsNullable(), args.User, args.Used)
            : HasComp<NCCyberwarePartComponent>(args.Used)
                ? TryInsertAssemblyPart(bench.AsNullable(), args.User, args.Used)
                : TryRepairAssembly(bench.AsNullable(), args.User, args.Used);
        args.Handled = true;
        if (!accepted)
            _assemblyPopup.PopupEntity(Loc.GetString("nc-cyberware-bench-rejected"), bench, args.User);
    }

    public bool CanOperateWorkbench(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user)
    {
        return Resolve(bench, ref bench.Comp, false) && !TerminatingOrDeleted(bench) &&
               Exists(user) && !TerminatingOrDeleted(user) &&
               _assemblyBlocker.CanInteract(user, bench) &&
               _assemblyInteraction.InRangeUnobstructed(user, bench.Owner, 2f);
    }

    public bool TryMountAssembly(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid moduleUid)
    {
        if (!CanOperateWorkbench(bench, user) || !Resolve(bench, ref bench.Comp, false) ||
            bench.Comp.Workpiece == null || bench.Comp.Workpiece.ContainedEntity != null ||
            !Exists(moduleUid) || TerminatingOrDeleted(moduleUid) ||
            !_assemblyHands.IsHolding(user, moduleUid) ||
            !TryComp<NCCyberwareComponent>(moduleUid, out var module) ||
            !TryGetProfile(module.Profile, out var profile) ||
            TryResolveOwningBody(moduleUid, out _))
            return false;
        InitializeState(moduleUid, module, profile);
        return _assemblyHands.TryDropIntoContainer(user, moduleUid, bench.Comp.Workpiece);
    }

    public bool TryInsertAssemblyPart(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid partUid)
    {
        if (!CanOperateWorkbench(bench, user) || !TryGetWorkpiece(bench, out var moduleUid, out var assembly) ||
            assembly.ConfigurationRevision == uint.MaxValue || !_assemblyHands.IsHolding(user, partUid) ||
            !Exists(partUid) || TerminatingOrDeleted(partUid) ||
            !TryComp<NCCyberwarePartComponent>(partUid, out var part) ||
            !_prototypes.TryIndex(part.Profile, out var profile) ||
            !assembly.Parts.TryGetValue(profile.Slot, out var container) || container.ContainedEntity != null)
            return false;
        return _assemblyHands.TryDropIntoContainer(user, partUid, container);
    }

    public bool TryRemoveAssemblyPart(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user,
        EntityUid expectedModule, ProtoId<NCCyberwarePartSlotPrototype> slot, uint expectedRevision)
    {
        if (!CanOperateWorkbench(bench, user) || !TryGetWorkpiece(bench, out var moduleUid, out var assembly) ||
            moduleUid != expectedModule || assembly.ConfigurationRevision != expectedRevision ||
            assembly.ConfigurationRevision == uint.MaxValue ||
            !assembly.Parts.TryGetValue(slot, out var container) || container.ContainedEntity is not { } partUid ||
            !_containers.Remove(partUid, container, destination: Transform(bench).Coordinates))
            return false;
        _assemblyHands.TryPickupAnyHand(user, partUid);
        return true;
    }

    public bool TryEjectAssembly(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid expectedModule)
    {
        if (!CanOperateWorkbench(bench, user) || !Resolve(bench, ref bench.Comp, false) ||
            bench.Comp.Workpiece?.ContainedEntity != expectedModule ||
            !_containers.Remove(expectedModule, bench.Comp.Workpiece, destination: Transform(bench).Coordinates))
            return false;
        _assemblyHands.TryPickupAnyHand(user, expectedModule);
        return true;
    }

    public bool TryTestAssembly(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user,
        EntityUid expectedModule, uint expectedRevision)
    {
        if (!CanOperateWorkbench(bench, user) || !TryGetWorkpiece(bench, out var moduleUid, out var assembly) ||
            moduleUid != expectedModule || assembly.ConfigurationRevision != expectedRevision)
            return false;
        RefreshAssembly(assembly);
        var module = Comp<NCCyberwareComponent>(moduleUid);
        var profile = _prototypes.Index(module.Profile);
        var integrity = Comp<NCOrganIntegrityComponent>(moduleUid);
        var passed = assembly.FunctionalEfficiency > 0f && integrity.CurrentIntegrity > 0f &&
                     module.Heat < profile.HeatShutdownThreshold && module.EmpDisabledUntil <= _timing.CurTime;
        if (passed && profile.RequiresPower)
            passed = _cells.TryGetBatteryFromSlot(bench.Owner, out var battery) &&
                     _battery.TryUseCharge(battery.Value.AsNullable(), Comp<NCCyberwareWorkbenchComponent>(bench).TestEnergy);
        assembly.ValidatedRevision = passed ? expectedRevision : null;
        RecordService(moduleUid, NCCyberwareServiceOperation.Tested, Guid.NewGuid());
        _assemblyPopup.PopupEntity(Loc.GetString(passed ? "nc-cyberware-bench-test-passed" :
            "nc-cyberware-bench-test-failed"), bench, user);
        return passed;
    }

    public bool TryRepairAssembly(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid material)
    {
        if (!CanOperateWorkbench(bench, user) || !TryGetWorkpiece(bench, out var moduleUid, out var assembly) ||
            !_assemblyHands.IsHolding(user, material) || !TryComp<StackComponent>(material, out var stack))
            return false;
        var module = Comp<NCCyberwareComponent>(moduleUid);
        var profile = _prototypes.Index(module.Profile);
        var definition = _prototypes.Index(profile.Assembly!.Value);
        if (stack.StackTypeId != definition.RepairMaterial || stack.Count < 1 || stack.Unlimited)
            return false;

        // Repair the first damaged real node in stable slot order. Parts keep their own permanent ceiling.
        var target = moduleUid;
        var ceiling = NCCyberwareCondition.ServiceCeiling(profile.MaximumIntegrity, module.PermanentDamage);
        var integrity = Comp<NCOrganIntegrityComponent>(target);
        if (integrity.CurrentIntegrity >= ceiling)
        {
            foreach (var container in assembly.Parts.OrderBy(pair => pair.Key.Id, StringComparer.Ordinal)
                         .Select(pair => pair.Value))
            {
                if (container.ContainedEntity is not { } partUid)
                    continue;
                var part = Comp<NCCyberwarePartComponent>(partUid);
                var partProfile = _prototypes.Index(part.Profile);
                var partIntegrity = Comp<NCOrganIntegrityComponent>(partUid);
                var partCeiling = NCCyberwareCondition.ServiceCeiling(partProfile.MaximumIntegrity, part.PermanentDamage);
                if (partIntegrity.CurrentIntegrity >= partCeiling)
                    continue;
                target = partUid;
                integrity = partIntegrity;
                ceiling = partCeiling;
                break;
            }
        }
        if (integrity.CurrentIntegrity >= ceiling || !_assemblyStacks.TryUse((material, stack), 1))
            return false;
        integrity.CurrentIntegrity = MathF.Min(ceiling, integrity.CurrentIntegrity + definition.RepairPerUnit);
        NormalizeMechanicalIntegrity(integrity);
        assembly.ValidatedRevision = null;
        RefreshAssembly(assembly);
        UpdateOperational(moduleUid, module, profile, _timing.CurTime);
        RecordService(moduleUid, NCCyberwareServiceOperation.Repaired, Guid.NewGuid());
        return true;
    }

    private bool TryGetWorkpiece(Entity<NCCyberwareWorkbenchComponent?> bench, out EntityUid module,
        out NCCyberwareAssemblyComponent assembly)
    {
        module = default;
        assembly = default!;
        if (!Resolve(bench, ref bench.Comp, false) || bench.Comp.Workpiece?.ContainedEntity is not { } workpiece ||
            TerminatingOrDeleted(workpiece) ||
            TryResolveOwningBody(workpiece, out _) ||
            !TryComp<NCCyberwareAssemblyComponent>(workpiece, out var resolved))
            return false;
        module = workpiece;
        assembly = resolved;
        return true;
    }

    private bool TryGetServiceWorkpiece(Entity<NCCyberwareWorkbenchComponent?> bench, out EntityUid module)
    {
        module = default;
        if (!Resolve(bench, ref bench.Comp, false) || bench.Comp.Workpiece?.ContainedEntity is not { } workpiece ||
            TerminatingOrDeleted(workpiece) || !HasComp<NCCyberwareComponent>(workpiece) ||
            TryResolveOwningBody(workpiece, out _)) return false;
        module = workpiece;
        return true;
    }

    private void OnWorkbenchVerbs(Entity<NCCyberwareWorkbenchComponent> bench, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract ||
            !TryGetServiceWorkpiece(bench.AsNullable(), out var moduleUid))
            return;
        var user = args.User;
        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("nc-cyberware-bench-eject"),
            Act = () => TryEjectAssembly(bench.AsNullable(), user, moduleUid),
        });
        if (!TryGetWorkpiece(bench.AsNullable(), out _, out var assembly)) return;
        var revision = assembly.ConfigurationRevision;
        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("nc-cyberware-bench-test"),
            Act = () => TryTestAssembly(bench.AsNullable(), user, moduleUid, revision),
        });
        foreach (var (slot, container) in assembly.Parts)
        {
            if (container.ContainedEntity == null)
                continue;
            var capturedSlot = slot;
            args.Verbs.Add(new InteractionVerb
            {
                Category = VerbCategory.Eject,
                Text = Loc.GetString(_prototypes.Index(slot).Name),
                Act = () => TryRemoveAssemblyPart(bench.AsNullable(), user, moduleUid, capturedSlot, revision),
            });
        }
    }

    private void OnWorkbenchExamined(Entity<NCCyberwareWorkbenchComponent> bench, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;
        if (!TryGetServiceWorkpiece(bench.AsNullable(), out var moduleUid))
        {
            args.PushMarkup(Loc.GetString("nc-cyberware-bench-empty"));
            return;
        }
        var module = Comp<NCCyberwareComponent>(moduleUid);
        var profile = _prototypes.Index(module.Profile);
        var frame = Comp<NCOrganIntegrityComponent>(moduleUid);
        if (!TryGetWorkpiece(bench.AsNullable(), out _, out var assembly))
        {
            args.PushMarkup(Loc.GetString("nc-cyberware-service-mounted", ("item", moduleUid)));
            return;
        }
        args.PushMarkup(Loc.GetString("nc-cyberware-bench-frame", ("item", moduleUid),
            ("current", frame.CurrentIntegrity),
            ("ceiling", NCCyberwareCondition.ServiceCeiling(profile.MaximumIntegrity, module.PermanentDamage)),
            ("nominal", profile.MaximumIntegrity), ("revision", assembly.ConfigurationRevision)));
        args.PushMarkup(Loc.GetString(assembly.ValidatedRevision == assembly.ConfigurationRevision
            ? "nc-cyberware-bench-tested" : "nc-cyberware-bench-untested"));
        foreach (var (slot, container) in assembly.Parts.OrderBy(pair => pair.Key.Id, StringComparer.Ordinal))
        {
            var slotName = Loc.GetString(_prototypes.Index(slot).Name);
            if (container.ContainedEntity is not { } partUid)
            {
                args.PushMarkup(Loc.GetString("nc-cyberware-bench-missing", ("slot", slotName)));
                continue;
            }
            var part = Comp<NCCyberwarePartComponent>(partUid);
            var partProfile = _prototypes.Index(part.Profile);
            args.PushMarkup(Loc.GetString("nc-cyberware-bench-part", ("slot", slotName), ("item", partUid),
                ("current", Comp<NCOrganIntegrityComponent>(partUid).CurrentIntegrity),
                ("ceiling", NCCyberwareCondition.ServiceCeiling(partProfile.MaximumIntegrity, part.PermanentDamage)),
                ("nominal", partProfile.MaximumIntegrity)));
        }
    }
}
