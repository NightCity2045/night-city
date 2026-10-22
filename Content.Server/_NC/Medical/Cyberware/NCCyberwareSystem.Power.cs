// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Emp;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.Components;
using Content.Shared.Power;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Containers;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private SharedBatterySystem _battery = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    [Dependency] private ItemSlotsSystem _powerSlots = default!;
    [Dependency] private HungerSystem _hunger = default!;

    private const float PowerEpsilon = 0.00001f;

    private void InitializePowerEvents()
    {
        SubscribeLocalEvent<NCCyberwarePowerBusComponent, EntInsertedIntoContainerMessage>(OnBusCellInserted);
        SubscribeLocalEvent<NCCyberwarePowerBusComponent, EntRemovedFromContainerMessage>(OnBusCellRemoved);
        SubscribeLocalEvent<NCCyberwareCellComponent, EmpPulseEvent>(OnCellEmp,
            before: [typeof(SharedBatterySystem)]);
        SubscribeLocalEvent<NCCyberwareCellComponent, ChargeChangedEvent>(OnBodyCellChargeChanged);
        SubscribeLocalEvent<NCCyberwarePowerStateComponent, ComponentShutdown>(OnPowerStateShutdown);
    }

    private void OnPowerStateShutdown(Entity<NCCyberwarePowerStateComponent> body, ref ComponentShutdown args)
    {
        _scheduled.Remove(body.Owner);
    }

    private void OnCellEmp(Entity<NCCyberwareCellComponent> cell, ref EmpPulseEvent args)
    {
        if (args.PulseId == Guid.Empty)
            return;
        if (cell.Comp.EmpPulses.Contains(args.PulseId))
        {
            args = args with { EnergyConsumption = 0f };
            return;
        }
        cell.Comp.EmpPulses.Enqueue(args.PulseId);
        while (cell.Comp.EmpPulses.Count > 128)
            cell.Comp.EmpPulses.Dequeue();
    }

    private void OnBusCellInserted(Entity<NCCyberwarePowerBusComponent> bus, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID is not (NCCyberwarePowerBusComponent.MainSlot or NCCyberwarePowerBusComponent.ReserveSlot))
            return;
        EnsureCellIdentity(args.Entity);
        RefreshBusBody(bus);
    }

    private void OnBusCellRemoved(Entity<NCCyberwarePowerBusComponent> bus, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID is NCCyberwarePowerBusComponent.MainSlot or NCCyberwarePowerBusComponent.ReserveSlot)
            RefreshBusBody(bus);
    }

    private void EnsureCellIdentity(EntityUid cell)
    {
        var identity = EnsureComp<NCCyberwareCellComponent>(cell);
        if (identity.StableInstanceId == Guid.Empty)
            identity.StableInstanceId = Guid.NewGuid();
    }

    private void OnBodyCellChargeChanged(Entity<NCCyberwareCellComponent> cell, ref ChargeChangedEvent args)
    {
        var holder = Transform(cell).ParentUid;
        if (!TryComp<NCCyberwarePowerBusComponent>(holder, out var bus) || bus.Restoring ||
            !TryResolveOwningBody(holder, out var body) ||
            TryComp<NCCyberwarePowerStateComponent>(body, out var state) && state.Advancing)
            return;
        RefreshBodyPower(body, _timing.CurTime);
        MarkBodyChanged(body);
    }

    private void RefreshBusBody(Entity<NCCyberwarePowerBusComponent> bus)
    {
        if (bus.Comp.Restoring || !TryResolveOwningBody(bus, out var body))
            return;
        RefreshBodyPower(body, _timing.CurTime);
        MarkBodyChanged(body);
    }

    private Entity<BatteryComponent>? GetBusCell(EntityUid holder, string slot)
    {
        if (!_powerSlots.TryGetSlot(holder, slot, out var itemSlot) || itemSlot.Item is not { } uid ||
            TerminatingOrDeleted(uid) || !TryComp<BatteryComponent>(uid, out var battery))
            return null;
        return (uid, battery);
    }

    private Entity<NCCyberwarePowerBusComponent>? FindPowerBus(IReadOnlyList<EntityUid> modules, TimeSpan now)
    {
        foreach (var uid in modules.OrderBy(uid => Comp<NCCyberwareComponent>(uid).StableInstanceId))
        {
            if (TryComp<NCCyberwarePowerBusComponent>(uid, out var bus) &&
                TryComp<NCCyberwareComponent>(uid, out var module) &&
                TryGetProfile(module.Profile, out var profile) && CanRunElectronics(uid, module, profile, now))
                return (uid, bus);
        }
        return null;
    }

    private bool CanRunElectronics(EntityUid uid, NCCyberwareComponent module, NCCyberwarePrototype profile, TimeSpan now)
    {
        return !TerminatingOrDeleted(uid) && module.EmpDisabledUntil <= now && !module.ThermalShutdown &&
               module.Heat < profile.HeatShutdownThreshold &&
               TryComp<NCOrganIntegrityComponent>(uid, out var integrity) && integrity.CurrentIntegrity > 0f &&
               (!TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly) || assembly.FunctionalEfficiency > 0f);
    }

    private List<NCCyberwarePowerGrant> AllocateBodyPower(IReadOnlyList<EntityUid> modules, TimeSpan now,
        out Entity<BatteryComponent>? main, out Entity<BatteryComponent>? reserve)
    {
        var bus = FindPowerBus(modules, now);
        main = bus is { } holder ? GetBusCell(holder, NCCyberwarePowerBusComponent.MainSlot) : null;
        reserve = bus is { } backup ? GetBusCell(backup, NCCyberwarePowerBusComponent.ReserveSlot) : null;
        var maximum = bus is { } active
            ? active.Comp.MaximumPower * NCCyberwareCondition.Efficiency(
                Comp<NCOrganIntegrityComponent>(active).CurrentIntegrity,
                _prototypes.Index(Comp<NCCyberwareComponent>(active).Profile).MaximumIntegrity)
            : 0f;
        var demands = new List<NCCyberwarePowerDemand>(modules.Count);
        foreach (var uid in modules)
        {
            var module = Comp<NCCyberwareComponent>(uid);
            var profile = _prototypes.Index(module.Profile);
            if (!profile.RequiresPower || module.LastAdvancedAt > now || !CanRunElectronics(uid, module, profile, now))
                continue;
            demands.Add(new NCCyberwarePowerDemand(module.StableInstanceId, profile.PowerPriority,
                profile.IdlePowerPerSecond + profile.LoadedPowerPerSecond * EffectivePowerLoad(module, profile)));
        }
        return NCCyberwarePowerMath.Allocate(demands, maximum,
            main is { } primary && _battery.GetCharge(primary.AsNullable()) > PowerEpsilon,
            reserve is { } secondary && _battery.GetCharge(secondary.AsNullable()) > PowerEpsilon);
    }

    /// <summary>Advances all consumers together once per observed body time, including snapshot checkpoints.</summary>
    public bool TryAdvanceBodyPower(EntityUid body, TimeSpan observedAt)
    {
        if (!HasComp<NCMedicalBodyComponent>(body) || TerminatingOrDeleted(body))
            return false;
        var state = EnsureComp<NCCyberwarePowerStateComponent>(body);
        if (!state.Initialized)
        {
            state.Initialized = true;
            state.LastAdvancedAt = observedAt;
        }
        if (state.Advancing || observedAt <= state.LastAdvancedAt)
            return false;
        var modules = CollectCyberware(body).Where(uid => !TerminatingOrDeleted(uid)).ToList();
        foreach (var uid in modules)
            InitializeState(uid, Comp<NCCyberwareComponent>(uid), _prototypes.Index(Comp<NCCyberwareComponent>(uid).Profile));
        var cursor = state.LastAdvancedAt;
        state.Advancing = true;
        try
        {
            while (cursor < observedAt)
            {
                foreach (var uid in modules)
                {
                    var module = Comp<NCCyberwareComponent>(uid);
                    var profile = _prototypes.Index(module.Profile);
                    UpdateNeuralState(module, profile, cursor);
                    if (module.Heat >= profile.HeatShutdownThreshold)
                        module.ThermalShutdown = true;
                    else if (module.Heat <= profile.HeatDeratingThreshold)
                        module.ThermalShutdown = false;
                }
                var seconds = (float)(observedAt - cursor).TotalSeconds;
                var converter = FindBioconverter(modules, cursor);
                // Conversion uses authoritative current nutrition. A one-second boundary also bounds feedback
                // when an empty battery starts charging; no accumulated offline conversion is stored.
                if (converter != null)
                    seconds = MathF.Min(seconds, 1f);
                var grants = AllocateBodyPower(modules, cursor, out var main, out var reserve);
                var mainRate = grants.Where(value => value.Source == NCCyberwarePowerSource.Main).Sum(value => value.Watts);
                var reserveRate = grants.Where(value => value.Source == NCCyberwarePowerSource.Reserve).Sum(value => value.Watts);
                if (main is { } primary && mainRate > 0f)
                    seconds = MathF.Min(seconds, _battery.GetCharge(primary.AsNullable()) / mainRate);
                if (reserve is { } secondary && reserveRate > 0f)
                    seconds = MathF.Min(seconds, _battery.GetCharge(secondary.AsNullable()) / reserveRate);
                var powered = grants.Where(value => value.Source != NCCyberwarePowerSource.None)
                    .Select(value => value.Id).ToHashSet();
                foreach (var uid in modules)
                {
                    var module = Comp<NCCyberwareComponent>(uid);
                    var profile = _prototypes.Index(module.Profile);
                    if (module.LastAdvancedAt > cursor)
                    {
                        seconds = MathF.Min(seconds, (float)(module.LastAdvancedAt - cursor).TotalSeconds);
                        continue;
                    }
                    if (module.EmpDisabledUntil > cursor)
                        seconds = MathF.Min(seconds, (float)(module.EmpDisabledUntil - cursor).TotalSeconds);
                    var generating = (!profile.RequiresPower || powered.Contains(module.StableInstanceId)) &&
                                     CanRunElectronics(uid, module, profile, cursor);
                    var rate = (generating ? profile.IdleHeatPerSecond + profile.LoadedHeatPerSecond * EffectivePowerLoad(module, profile) : 0f) -
                               profile.PassiveCoolingPerSecond;
                    var threshold = rate > 0f ? profile.HeatShutdownThreshold : profile.HeatDeratingThreshold;
                    if (rate != 0f && (threshold - module.Heat) / rate > PowerEpsilon)
                        seconds = MathF.Min(seconds, (threshold - module.Heat) / rate);
                }
                if (seconds <= 0f || !float.IsFinite(seconds))
                    break;
                // Each physical battery is debited once for the whole consumer set in this segment.
                if (main is { } mainCell && mainRate > 0f)
                    _battery.UseCharge(mainCell.AsNullable(), mainRate * seconds);
                if (reserve is { } reserveCell && reserveRate > 0f)
                    _battery.UseCharge(reserveCell.AsNullable(), reserveRate * seconds);
                foreach (var uid in modules)
                {
                    var module = Comp<NCCyberwareComponent>(uid);
                    if (module.LastAdvancedAt > cursor)
                        continue;
                    var profile = _prototypes.Index(module.Profile);
                    var generating = (!profile.RequiresPower || powered.Contains(module.StableInstanceId)) &&
                                     CanRunElectronics(uid, module, profile, cursor);
                    var rate = (generating ? profile.IdleHeatPerSecond + profile.LoadedHeatPerSecond * EffectivePowerLoad(module, profile) : 0f) -
                               profile.PassiveCoolingPerSecond;
                    var thermal = NCCyberwarePowerMath.AdvanceHeat(module.Heat, rate, seconds,
                        profile.HeatCapacity, profile.HeatDamageThreshold, profile.HeatDamagePerSecond);
                    module.Heat = thermal.Heat;
                    if (thermal.Damage > 0f && TryComp<NCOrganIntegrityComponent>(uid, out var integrity))
                    {
                        integrity.CurrentIntegrity = MathF.Max(0f, integrity.CurrentIntegrity - thermal.Damage);
                        DamageAssembly(uid, module, profile, thermal.Damage, continuousHeat: true);
                        NormalizeMechanicalIntegrity(integrity);
                    }
                }
                if (converter is { } conversion && main is { } charging)
                    ConvertNutrition(body, conversion, charging, seconds);
                var ticks = Math.Max(1L, (long)(seconds * TimeSpan.TicksPerSecond));
                cursor = TimeSpan.FromTicks(Math.Min(observedAt.Ticks, cursor.Ticks + ticks));
            }
            state.LastAdvancedAt = observedAt;
            foreach (var uid in modules)
                Comp<NCCyberwareComponent>(uid).LastAdvancedAt = observedAt;
            state.Advancing = false;
            RefreshBodyPower(body, observedAt, modules);
            MarkBodyChanged(body);
            return true;
        }
        finally
        {
            state.Advancing = false;
        }
    }

    private Entity<NCCyberwareBioconverterComponent>? FindBioconverter(IReadOnlyList<EntityUid> modules, TimeSpan now)
    {
        foreach (var uid in modules.OrderBy(uid => Comp<NCCyberwareComponent>(uid).StableInstanceId))
        {
            if (!TryComp<NCCyberwareBioconverterComponent>(uid, out var converter))
                continue;
            var module = Comp<NCCyberwareComponent>(uid);
            var profile = _prototypes.Index(module.Profile);
            if (module.LastAdvancedAt <= now && CanRunElectronics(uid, module, profile, now))
                return (uid, converter);
        }
        return null;
    }

    private void ConvertNutrition(EntityUid body, Entity<NCCyberwareBioconverterComponent> converter,
        Entity<BatteryComponent> battery, float seconds)
    {
        if (!TryComp<HungerComponent>(body, out var hunger))
            return;
        var module = Comp<NCCyberwareComponent>(converter);
        var profile = _prototypes.Index(module.Profile);
        var efficiency = NCCyberwareCondition.Efficiency(Comp<NCOrganIntegrityComponent>(converter).CurrentIntegrity,
            profile.MaximumIntegrity);
        var before = _hunger.GetHunger(hunger);
        var energy = NCCyberwarePowerMath.ConversionEnergy(seconds, converter.Comp.MaximumPower * efficiency,
            before, converter.Comp.ProtectedNutrition, converter.Comp.EnergyPerNutrition,
            battery.Comp.MaxCharge - _battery.GetCharge(battery.AsNullable()));
        if (energy <= 0f)
            return;
        _hunger.ModifyHunger(body, -energy / converter.Comp.EnergyPerNutrition, hunger);
        var spent = MathF.Max(0f, before - _hunger.GetHunger(hunger));
        var accepted = _battery.ChangeCharge(battery.AsNullable(), spent * converter.Comp.EnergyPerNutrition);
        // A concurrent synchronous charge callback may reduce free capacity. Return unused nutrition.
        if (accepted < spent * converter.Comp.EnergyPerNutrition)
            _hunger.ModifyHunger(body, spent - MathF.Max(0f, accepted) / converter.Comp.EnergyPerNutrition, hunger);
    }

    private void RefreshBodyPower(EntityUid body, TimeSpan now, IReadOnlyList<EntityUid>? modules = null)
    {
        modules ??= CollectCyberware(body);
        var grants = AllocateBodyPower(modules, now, out _, out _)
            .Where(value => value.Source != NCCyberwarePowerSource.None).Select(value => value.Id).ToHashSet();
        foreach (var uid in modules)
        {
            var module = Comp<NCCyberwareComponent>(uid);
            var profile = _prototypes.Index(module.Profile);
            module.PowerGranted = !profile.RequiresPower || grants.Contains(module.StableInstanceId);
            module.CurrentPower = 0f;
            UpdateNeuralState(module, profile, now);
            UpdateOperational(uid, module, profile, now);
        }
        UpdatePowerWarning(body, modules, now);
    }

    private void UpdatePowerWarning(EntityUid body, IReadOnlyList<EntityUid> modules, TimeSpan now)
    {
        if (!TryComp<NCCyberwarePowerStateComponent>(body, out var state) || state.Advancing ||
            !modules.Any(uid => _prototypes.Index(Comp<NCCyberwareComponent>(uid).Profile).RequiresPower))
            return;
        var bus = FindPowerBus(modules, now);
        var main = bus is { } holder ? GetBusCell(holder, NCCyberwarePowerBusComponent.MainSlot) : null;
        var reserve = bus is { } backup ? GetBusCell(backup, NCCyberwarePowerBusComponent.ReserveSlot) : null;
        var warning = NCCyberwarePowerWarning.None;
        if (main is not { } primary || _battery.GetCharge(primary.AsNullable()) <= PowerEpsilon)
            warning = reserve is { } secondary && _battery.GetCharge(secondary.AsNullable()) > PowerEpsilon
                ? NCCyberwarePowerWarning.Reserve : NCCyberwarePowerWarning.Unavailable;
        else if (_battery.GetChargeLevel(primary.AsNullable()) <= bus!.Value.Comp.LowChargeFraction)
            warning = NCCyberwarePowerWarning.Low;
        if (warning == state.Warning)
            return;
        state.Warning = warning;
        var message = warning switch
        {
            NCCyberwarePowerWarning.Low => "nc-cyberware-power-warning-low",
            NCCyberwarePowerWarning.Reserve => "nc-cyberware-power-warning-reserve",
            NCCyberwarePowerWarning.Unavailable => "nc-cyberware-power-warning-unavailable",
            _ => null,
        };
        if (message != null)
            _assemblyPopup.PopupEntity(Loc.GetString(message), body, body);
    }

    private static float EffectivePowerLoad(NCCyberwareComponent module, NCCyberwarePrototype profile)
    {
        return profile.NeuralRequirement == NCCyberwareNeuralRequirement.Required &&
               (!module.Calibrated || module.NeuralIntegrity < profile.NeuralMinimumOperationalIntegrity ||
                module.NeuralState is NCCyberwareNeuralState.Disconnected or NCCyberwareNeuralState.Disrupted)
            ? 0f : module.Load;
    }

    public float GetPowerFraction(EntityUid moduleUid)
    {
        if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) || !TryGetProfile(module.Profile, out var profile))
            return 0f;
        if (!profile.RequiresPower)
            return 1f;
        if (!TryResolveOwningBody(moduleUid, out var body) || !module.PowerGranted ||
            FindPowerBus(CollectCyberware(body), _timing.CurTime) is not { } bus)
            return 0f;
        var cell = GetBusCell(bus, NCCyberwarePowerBusComponent.MainSlot);
        if (cell == null || _battery.GetCharge(cell.Value.AsNullable()) <= PowerEpsilon)
            cell = profile.PowerPriority == NCCyberwarePowerPriority.LifeSupport
                ? GetBusCell(bus, NCCyberwarePowerBusComponent.ReserveSlot) : null;
        return cell is { } source ? _battery.GetChargeLevel(source.AsNullable()) : 0f;
    }

    public float GetSupplyCharge(EntityUid moduleUid)
    {
        if (!TryComp<NCCyberwareComponent>(moduleUid, out var module) || !TryResolveOwningBody(moduleUid, out var body) ||
            FindPowerBus(CollectCyberware(body), _timing.CurTime) is not { } bus)
            return 0f;
        var cell = GetBusCell(bus, NCCyberwarePowerBusComponent.MainSlot);
        if (cell == null || _battery.GetCharge(cell.Value.AsNullable()) <= PowerEpsilon)
            cell = _prototypes.Index(module.Profile).PowerPriority == NCCyberwarePowerPriority.LifeSupport
                ? GetBusCell(bus, NCCyberwarePowerBusComponent.ReserveSlot) : null;
        return cell is { } source ? _battery.GetCharge(source.AsNullable()) : 0f;
    }

    private void ApplyBusEmp(EntityUid body, in NCCyberwareEmpRequest request)
    {
        foreach (var holder in CollectCyberware(body).Where(uid => HasComp<NCCyberwarePowerBusComponent>(uid)))
        {
            foreach (var slot in new[] { NCCyberwarePowerBusComponent.MainSlot, NCCyberwarePowerBusComponent.ReserveSlot })
            {
                if (GetBusCell(holder, slot) is not { } cell)
                    continue;
                EnsureCellIdentity(cell);
                var pulse = new EmpPulseEvent(request.Energy, false, false, request.Duration, null, request.ResolutionId.Value);
                RaiseLocalEvent(cell.Owner, ref pulse);
            }
        }
    }
}
