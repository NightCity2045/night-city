// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.DoAfter;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private SharedDoAfterSystem _factoryDoAfter = default!;
    [Dependency] private SharedAppearanceSystem _factoryAppearance = default!;
    [Dependency] private MetaDataSystem _factoryMetadata = default!;

    private void RefreshFactoryAppearance(EntityUid uid, NCCyberwareComponent module)
    {
        if (!HasComp<AppearanceComponent>(uid) || !_prototypes.TryIndex(module.Profile, out var profile) ||
            !profile.RequiresFactoryPreparation) return;
        _factoryAppearance.SetData(uid, NCCyberwareProductionVisuals.Prepared, module.FactoryPrepared);
        if (profile.NameId is { } name)
            _factoryMetadata.SetEntityName(uid, Loc.GetString("nc-cyber-production-item-name",
                ("implant", Loc.GetString(name)), ("state", Loc.GetString(module.FactoryPrepared
                    ? "nc-cyber-production-state-ready" : "nc-cyber-production-state-unassembled"))));
    }


    public bool CanPrepareFactoryKit(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid wrap,
        EntityUid expectedModule)
    {
        if (!CanOperateWorkbench(bench, user) || !TryGetServiceWorkpiece(bench, out var uid) || uid != expectedModule ||
            !_assemblyHands.IsHolding(user, wrap) || !HasComp<NCCyberwareSterileWrapComponent>(wrap) ||
            !TryComp<NCCyberwareComponent>(uid, out var module) || module.FactoryPrepared ||
            !TryGetProfile(module.Profile, out var profile) || !profile.RequiresFactoryPreparation ||
            module.Service.Revision == uint.MaxValue || module.EmpDisabledUntil > _timing.CurTime ||
            module.ThermalShutdown || module.Heat >= profile.HeatDeratingThreshold ||
            !TryComp<NCOrganIntegrityComponent>(uid, out var integrity) ||
            NCCyberwareCondition.Efficiency(integrity.CurrentIntegrity, profile.MaximumIntegrity) < profile.FactoryMinimumEfficiency ||
            TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly) &&
                (assembly.ValidatedRevision != assembly.ConfigurationRevision || assembly.FunctionalEfficiency < profile.FactoryMinimumEfficiency) ||
            !_cells.TryGetBatteryFromSlot(bench.Owner, out var battery)) return false;
        return _battery.GetCharge(battery.Value.AsNullable()) >= profile.FactoryTestEnergy;
    }

    public bool TryStartFactoryPreparation(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid wrap)
    {
        if (!TryGetServiceWorkpiece(bench, out var module) || !CanPrepareFactoryKit(bench, user, wrap, module)) return false;
        var state = Comp<NCCyberwareComponent>(module);
        return _factoryDoAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user,
            TimeSpan.FromSeconds(_prototypes.Index(state.Profile).FactoryTestSeconds),
            new NCCyberwarePreparationDoAfterEvent { Module = GetNetEntity(module), ServiceRevision = state.Service.Revision },
            bench.Owner, target: bench.Owner, used: wrap)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        });
    }

    private void OnFactoryPreparation(Entity<NCCyberwareWorkbenchComponent> bench, ref NCCyberwarePreparationDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || args.Used is not { } wrap) return;
        args.Handled = TryPrepareFactoryKit(bench.AsNullable(), args.User, wrap, GetEntity(args.Module), args.ServiceRevision);
    }

    public bool TryPrepareFactoryKit(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid wrap,
        EntityUid expectedModule, uint expectedRevision)
    {
        if (!CanPrepareFactoryKit(bench, user, wrap, expectedModule) ||
            !TryComp<NCCyberwareComponent>(expectedModule, out var module) || module.Service.Revision != expectedRevision ||
            !_cells.TryGetBatteryFromSlot(bench.Owner, out var battery) ||
            !_assemblyHands.TryDrop(user, wrap)) return false;
        if (!_battery.TryUseCharge(battery.Value.AsNullable(), _prototypes.Index(module.Profile).FactoryTestEnergy)) return false;
        QueueDel(wrap);
        module.FactoryPrepared = true;
        RecordService(expectedModule, NCCyberwareServiceOperation.Prepared, Guid.NewGuid());
        _assemblyPopup.PopupEntity(Loc.GetString("nc-cyber-production-prepared"), bench, user);
        return true;
    }
}
