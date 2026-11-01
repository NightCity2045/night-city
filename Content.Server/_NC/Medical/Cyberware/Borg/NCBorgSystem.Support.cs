// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Borg;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Body.Events;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Emp;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Power;
using Content.Shared.Power.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.PowerCell;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Cyberware.Borg;

public sealed partial class NCBorgSystem : EntitySystem
{
    public const string SupportSolution = "nc_biopod_support";
    public static readonly ProtoId<ReagentPrototype> Perfusate = "NCBiopodPerfusate";
    public static readonly ProtoId<ReagentPrototype> Oxygen = "Oxygen";
    public static readonly ProtoId<ReagentPrototype> Dialysate = "NCBiopodDialysate";
    public static readonly ProtoId<ReagentPrototype> Coolant = "NCBiopodCoolant";
    private static readonly string[] SupportCellSlots =
        [NCCyberwarePowerBusComponent.MainSlot, NCCyberwarePowerBusComponent.ReserveSlot];

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedBatterySystem _battery = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    [Dependency] private HungerSystem _hunger = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;
    [Dependency] private SharedNCBorgSystem _shared = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private ItemSlotsSystem _supportSlots = default!;
    [Dependency] private NCMedicalBodySystem _borgMedicalBodies = default!;

    public override void Initialize()
    {
        base.Initialize();
        InitializeTransfers();
        SubscribeLocalEvent<NCBorgCoreComponent, MapInitEvent>(OnCoreMapInit,
            after: [typeof(InitialBodySystem), typeof(HungerSystem), typeof(SharedBloodstreamSystem)]);
        SubscribeLocalEvent<NCBorgCoreComponent, NCMedicalScheduledUpdateEvent>(OnScheduledUpdate,
            before: [typeof(NCPharmacologySystem)]);
        SubscribeLocalEvent<NCBorgCoreComponent, NCMedicalNextUpdateEvent>(OnNextUpdate);
        SubscribeLocalEvent<NCBorgCoreComponent, NCMedicalBodyChangedEvent>(OnBodyChanged);
        SubscribeLocalEvent<NCBorgCoreComponent, ExaminedEvent>(OnCoreExamined);
        SubscribeLocalEvent<NCBorgCoreComponent, EmpPulseEvent>(OnCoreEmp);
        SubscribeLocalEvent<NCBorgCoreComponent, ChargeChangedEvent>(OnCoreChargeChanged);
        SubscribeLocalEvent<NCBorgCoreComponent, ComponentShutdown>(OnCoreShutdown);
        SubscribeLocalEvent<NCBorgCoreComponent, PowerCellChangedEvent>(OnCoreCellChanged);
        SubscribeLocalEvent<NCBorgSupportProviderComponent, MetabolizerAttemptEvent>(OnMetabolizerAttempt);
        SubscribeLocalEvent<NCBorgShellSocketComponent, OrganInsertedIntoEvent>(OnShellOrganInserted);
        SubscribeLocalEvent<NCBorgShellSocketComponent, OrganRemovedFromEvent>(OnShellOrganRemoved);
        SubscribeLocalEvent<NCBorgShellSocketComponent, MapInitEvent>(OnShellMapInit,
            after: [typeof(InitialBodySystem), typeof(HungerSystem), typeof(SharedBloodstreamSystem)]);
    }

    private void OnCoreMapInit(Entity<NCBorgCoreComponent> core, ref MapInitEvent args)
    {
        if (!TryRestoreCoreIdentity(core.AsNullable(), core.Comp.CoreId == Guid.Empty ? Guid.NewGuid() : core.Comp.CoreId))
        {
            Log.Warning("Refused duplicate physical Borg CoreId at manufacture.");
            QueueDel(core);
            return;
        }
        core.Comp.LastAdvancedAt = _timing.CurTime;
        // Manufactured carriers have no assimilated food. Ordinary ingestion or the service mixture supplies it.
        if (TryComp<HungerComponent>(core, out var hunger))
            _hunger.SetHunger(core, 0f, hunger);
        RefreshPowerBinding(core);
        RefreshSupport(core);
        _scheduler.Schedule(core, TimeSpan.FromSeconds(1));
    }

    private void OnNextUpdate(Entity<NCBorgCoreComponent> core, ref NCMedicalNextUpdateEvent args)
    {
        if (args.Delay == null || args.Delay > TimeSpan.FromSeconds(1))
            args.Delay = TimeSpan.FromSeconds(1);
    }

    private void OnScheduledUpdate(Entity<NCBorgCoreComponent> core, ref NCMedicalScheduledUpdateEvent args)
    {
        var elapsed = _timing.CurTime - core.Comp.LastAdvancedAt;
        TryAdvanceSupport(core.AsNullable(), args.Elapsed > elapsed ? args.Elapsed : elapsed);
    }

    /// <summary>Advances only online, scheduler-measured time. Every joule comes from the inserted real cell.</summary>
    public bool TryAdvanceSupport(Entity<NCBorgCoreComponent?> core, TimeSpan elapsed)
    {
        if (!Resolve(core, ref core.Comp, false) || elapsed < TimeSpan.Zero)
            return false;
        var seconds = (float) elapsed.TotalSeconds;
        if (!float.IsFinite(seconds))
            return false;
        if (seconds > 0f)
        {
            var poweredSeconds = core.Comp.EmpDisabledUntil > _timing.CurTime
                ? 0f
                : NCBorgSupport.SuppliedSeconds(GetSupportCharge(core), core.Comp.PowerPerSecond, seconds);
            if (poweredSeconds > 0f)
                TryUseSupportCharge(core, poweredSeconds * core.Comp.PowerPerSecond);
            Consume(core, Perfusate, core.Comp.PerfusatePerSecond * poweredSeconds);
            Consume(core, Oxygen, core.Comp.OxygenPerSecond * poweredSeconds);
            Consume(core, Dialysate, core.Comp.DialysatePerSecond * poweredSeconds);
            var cooled = Consume(core, Coolant, core.Comp.CoolantPerSecond * poweredSeconds);
            // Pump heat remains in the physical pod across frame changes. Loss of thermoregulation harms its brain.
            var livingBrain = core.Comp.Occupied &&
                              (!TryComp<NCPhysiologyComponent>(core, out var physiology) || !physiology.Irreversible);
            core.Comp.Temperature = NCBorgThermalMath.Advance(core.Comp.Temperature, seconds, poweredSeconds,
                livingBrain, cooled);
        }
        core.Comp.LastAdvancedAt = _timing.CurTime;
        RefreshSupport((core.Owner, core.Comp));
        if (core.Comp.Occupied && seconds > 0f && core.Comp.Clearance < 0.5f)
            _systemic.Apply(core, "NCToxinSystemicDamage", seconds * 0.5f * (1f - core.Comp.Clearance));
        return true;
    }

    private float Consume(Entity<NCBorgCoreComponent?> core, ProtoId<ReagentPrototype> reagent, float amount)
    {
        if (amount <= 0f || core.Comp == null ||
            !_solutions.TryGetSolution(core.Owner, SupportSolution, out var solution, out _))
            return 0f;
        var requested = amount + core.Comp.ConsumptionRemainders.GetValueOrDefault(reagent);
        var units = FixedPoint2.New(MathF.Floor(requested * 100f) / 100f);
        var removed = _solutions.RemoveReagent(solution.Value, reagent.Id, units).Float();
        core.Comp.ConsumptionRemainders[reagent] = MathF.Max(0f, requested - units.Float());
        return removed;
    }

    public void RefreshSupport(Entity<NCBorgCoreComponent> core)
    {
        core.Comp.Powered = core.Comp.EmpDisabledUntil <= _timing.CurTime &&
                            GetSupportCharge(core) > 0f;
        _solutions.TryGetSolution(core.Owner, SupportSolution, out _, out var medium);
        var perfusion = Available(medium, Perfusate) && core.Comp.Powered ? 1f : 0f;
        var nutrition = TryComp<HungerComponent>(core, out var hunger)
            ? Math.Clamp(_hunger.GetHunger(hunger) / MathF.Max(1f, hunger.Thresholds[HungerThreshold.Starving]), 0f, 1f)
            : 0f;
        var temperature = Math.Clamp(1f - MathF.Abs(core.Comp.Temperature - 310.15f) / 8f, 0f, 1f);
        core.Comp.Perfusion = perfusion * MathF.Min(nutrition, temperature) *
                              GetProviderEfficiency(core, NCOrganFunctionType.Circulation);
        core.Comp.Respiration = Available(medium, Oxygen) && core.Comp.Powered
            ? GetProviderEfficiency(core, NCOrganFunctionType.Respiration) : 0f;
        core.Comp.Clearance = Available(medium, Dialysate) && core.Comp.Powered
            ? MathF.Min(GetProviderEfficiency(core, NCOrganFunctionType.Metabolism),
                GetProviderEfficiency(core, NCOrganFunctionType.Filtration)) : 0f;
        var hasShell = _anatomy.TryGetOrgan(core, "Torso", out _);
        _appearance.SetData(core.Owner, NCBorgVisuals.Shell, hasShell);
        _shared.SetFoodProcessor(core, hasShell &&
            _anatomy.TryGetOrgan(core, "Tongue", out var mouth) &&
            TryComp<NCOrganFunctionComponent>(mouth, out var function) && function.CurrentCapacity > 0f &&
            (!TryComp<NCCyberwareComponent>(mouth, out var mechanical) || mechanical.Operational), hasShell);
    }

    private static bool Available(Solution? solution, ProtoId<ReagentPrototype> reagent)
        => solution?.GetTotalPrototypeQuantity(reagent) > FixedPoint2.Zero;

    private void OnBodyChanged(Entity<NCBorgCoreComponent> core, ref NCMedicalBodyChangedEvent args)
        => RefreshSupport(core);

    private void OnCoreEmp(Entity<NCBorgCoreComponent> core, ref EmpPulseEvent args)
    {
        if (args.Duration <= TimeSpan.Zero || args.EnergyConsumption <= 0f)
            return;
        core.Comp.EmpDisabledUntil = TimeSpan.FromTicks(Math.Max(core.Comp.EmpDisabledUntil.Ticks,
            (_timing.CurTime + args.Duration).Ticks));
        RefreshSupport(core);
        _scheduler.ProcessNow(core, TimeSpan.Zero);
        args.Affected = true;
    }

    private void OnCoreChargeChanged(Entity<NCBorgCoreComponent> core, ref ChargeChangedEvent args)
        => RefreshSupport(core);

    private void OnCoreCellChanged(Entity<NCBorgCoreComponent> core, ref PowerCellChangedEvent args)
        => RefreshSupport(core);

    private void OnMetabolizerAttempt(Entity<NCBorgSupportProviderComponent> provider, ref MetabolizerAttemptEvent args)
    {
        if (!TryComp<OrganComponent>(provider, out var organ) || organ.Body is not { } body ||
            !TryComp<NCBorgCoreComponent>(body, out var core) ||
            !TryComp<NCOrganFunctionComponent>(provider, out var function) ||
            !core.Powered || function.CurrentCapacity <= 0f ||
            !TryComp<NCOrganIntegrityComponent>(provider, out var integrity) || integrity.FunctionalEfficiency <= 0f ||
            TryComp<NCCyberwareComponent>(provider, out var mechanical) && !mechanical.Operational ||
            function.FunctionType is NCOrganFunctionType.Metabolism or NCOrganFunctionType.Filtration && core.Clearance <= 0f)
            args.Cancelled = true;
    }

    private float GetProviderEfficiency(EntityUid core, NCOrganFunctionType type)
    {
        if (!TryComp<BodyComponent>(core, out var body) || body.Organs == null)
            return 0f;
        foreach (var organ in body.Organs.ContainedEntities)
        {
            if (!HasComp<NCBorgSupportProviderComponent>(organ) ||
                !TryComp<NCOrganFunctionComponent>(organ, out var function) || function.FunctionType != type)
                continue;
            var efficiency = function.BaseCapacity > 0f
                ? Math.Clamp(function.CurrentCapacity / function.BaseCapacity, 0f, 1f) : 0f;
            if (TryComp<NCOrganIntegrityComponent>(organ, out var integrity))
                efficiency = MathF.Min(efficiency, integrity.FunctionalEfficiency);
            if (TryComp<NCCyberwareComponent>(organ, out var mechanical))
                efficiency = MathF.Min(efficiency, mechanical.FunctionalEfficiency);
            return efficiency;
        }
        return 0f;
    }

    private void OnCoreExamined(Entity<NCBorgCoreComponent> core, ref ExaminedEvent args)
    {
        RefreshSupport(core);
        _solutions.TryGetSolution(core.Owner, SupportSolution, out _, out var solution);
        args.PushMarkup(Loc.GetString("nc-borg-support-examine",
            ("charge", MathF.Round(GetSupportCharge(core))),
            ("medium", solution?.GetTotalPrototypeQuantity(Perfusate).Float() ?? 0f),
            ("oxygen", solution?.GetTotalPrototypeQuantity(Oxygen).Float() ?? 0f),
            ("filter", solution?.GetTotalPrototypeQuantity(Dialysate).Float() ?? 0f),
            ("coolant", solution?.GetTotalPrototypeQuantity(Coolant).Float() ?? 0f),
            ("temperature", MathF.Round(core.Comp.Temperature - 273.15f, 1))));
    }

    private void OnShellMapInit(Entity<NCBorgShellSocketComponent> socket, ref MapInitEvent args)
    {
        RefreshPowerBinding(socket);
        RefreshShell(socket);
    }
    private void OnShellOrganInserted(Entity<NCBorgShellSocketComponent> socket, ref OrganInsertedIntoEvent args) => RefreshShell(socket);
    private void OnShellOrganRemoved(Entity<NCBorgShellSocketComponent> socket, ref OrganRemovedFromEvent args) => RefreshShell(socket);
    private void RefreshShell(EntityUid socket) => _appearance.SetData(socket, NCBorgVisuals.Shell,
        _anatomy.TryGetOrgan(socket, "Torso", out _));

    private void RefreshPowerBinding(EntityUid body)
    {
        _cyberware.ResetPowerClock(body);
        _borgMedicalBodies.TryMarkDirty(body,
            NCMedicalDirtyDomains.OrganFunction | NCMedicalDirtyDomains.Diagnostics | NCMedicalDirtyDomains.Persistence);
    }

    private Entity<BatteryComponent>? GetSupportCell(EntityUid core, string slotId)
    {
        if (!_supportSlots.TryGetSlot(core, slotId, out var slot) || slot.Item is not { } uid ||
            !TryComp<BatteryComponent>(uid, out var battery))
            return null;
        return (uid, battery);
    }

    private float GetSupportCharge(EntityUid core)
    {
        var main = GetSupportCell(core, NCCyberwarePowerBusComponent.MainSlot);
        var reserve = GetSupportCell(core, NCCyberwarePowerBusComponent.ReserveSlot);
        return (main is { } primary ? _battery.GetCharge(primary.AsNullable()) : 0f) +
               (reserve is { } secondary ? _battery.GetCharge(secondary.AsNullable()) : 0f);
    }

    private bool TryUseSupportCharge(EntityUid core, float amount)
    {
        if (GetSupportCharge(core) < amount)
            return false;
        foreach (var slot in SupportCellSlots)
        {
            if (amount <= 0f || GetSupportCell(core, slot) is not { } cell)
                continue;
            var taken = MathF.Min(amount, _battery.GetCharge(cell.AsNullable()));
            _battery.UseCharge(cell.AsNullable(), taken);
            amount -= taken;
        }
        return amount <= 0.00001f;
    }
}
