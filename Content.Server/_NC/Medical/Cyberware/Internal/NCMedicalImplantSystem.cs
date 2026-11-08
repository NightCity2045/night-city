// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Internal;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.ActionBlocker;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Cyberware.Internal;

[RegisterComponent]
public sealed partial class NCMedicalCartridgeComponent : Component;

/// <summary>Medical hardware retains authority over chemistry, dosing budgets and fresh sensor readings.</summary>
public sealed partial class NCMedicalImplantSystem : EntitySystem
{
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private NCMedicalBodySystem _medical = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private SharedBloodstreamSystem _blood = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private float _elapsed;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMedicalImplantComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<NCCyberwareComponent, ExaminedEvent>(OnCyberwareExamined);
        SubscribeLocalEvent<NCCyberwarePartComponent, ExaminedEvent>(OnPartExamined);
    }

    public bool IsReady(EntityUid uid, out EntityUid body)
    {
        body = default;
        return TryComp<NCCyberwareComponent>(uid, out var hardware) && hardware.Operational &&
               !hardware.CrisisBlocksActiveModes && _cyberware.FirmwareAllowsActiveModes(uid) &&
               _cyberware.MedicalControllerReady(uid) && _cyberware.TryGetOwningBody(uid, out body) &&
               TryComp<NCPhysiologyComponent>(body, out var physiology) && !physiology.Irreversible;
    }

    public override void Update(float frameTime)
    {
        _elapsed += frameTime;
        if (_elapsed < .5f) return;
        var elapsed = _elapsed;
        _elapsed = 0;
        var query = EntityQueryEnumerator<NCMedicalImplantComponent>();
        while (query.MoveNext(out var uid, out var module))
        {
            if (IsPaused(uid)) continue;
            var previousCooldown = module.State.CooldownSeconds;
            module.State.CooldownSeconds = MathF.Max(0, previousCooldown - elapsed);
            if (MathF.Ceiling(previousCooldown / 5) != MathF.Ceiling(module.State.CooldownSeconds / 5) &&
                _cyberware.TryGetOwningBody(uid, out var cooldownOwner)) Changed(cooldownOwner);
            if (!IsReady(uid, out var body)) { module.HasSample = false; continue; }
            if (!module.Injector)
            {
                if (_timing.CurTime < module.NextSample) continue;
                var quality = _cyberware.MedicalPartQuality(uid, "NCMedicalSensorSlot");
                module.NextSample = _timing.CurTime + TimeSpan.FromSeconds(2 / quality.Performance);
                if (!TryComp<NCPhysiologyComponent>(body, out var physiology) ||
                    !TryComp<NCCirculationComponent>(body, out var circulation)) { module.HasSample = false; continue; }
                module.Oxygen = NCMedicalImplantMath.Quantize(physiology.Oxygenation * 100, quality.MeasurementStep);
                module.Perfusion = NCMedicalImplantMath.Quantize(circulation.Perfusion * 100, quality.MeasurementStep);
                module.Pain = NCMedicalImplantMath.Quantize(physiology.TotalPain, quality.MeasurementStep);
                module.Blood = NCMedicalImplantMath.Quantize(circulation.BloodVolume, quality.MeasurementStep);
                module.SampledAt = _timing.CurTime;
                module.HasSample = true;
                if ((module.Oxygen < 80 || module.Perfusion < 70) && _timing.CurTime >= module.NextAlarm)
                {
                    module.NextAlarm = _timing.CurTime + TimeSpan.FromSeconds(30);
                    ShowReading(body, module);
                }
            }
            else if (module.State.Automatic && TryGetReading(body, out var reading) && Triggered(module.State, reading))
                TryInject(uid, body, automatic: true);
        }
    }

    private static bool Triggered(NCMedicalImplantState state, NCMedicalImplantComponent reading) => state.Trigger switch
    {
        NCMedicalTrigger.OxygenBelow => reading.Oxygen < state.Threshold,
        NCMedicalTrigger.PerfusionBelow => reading.Perfusion < state.Threshold,
        NCMedicalTrigger.PainAbove => reading.Pain > state.Threshold,
        _ => false,
    };

    public bool TryGetReading(EntityUid body, out NCMedicalImplantComponent reading)
    {
        foreach (var uid in _cyberware.GetInstalledModuleEntities(body, 32))
            if (TryComp<NCMedicalImplantComponent>(uid, out var candidate) && !candidate.Injector && candidate.HasSample &&
                _timing.CurTime - candidate.SampledAt <= TimeSpan.FromSeconds(5) && IsReady(uid, out var owner) && owner == body)
            { reading = candidate; return true; }
        reading = default!;
        return false;
    }

    public bool TryInject(EntityUid uid, EntityUid user, bool automatic = false)
    {
        if (!automatic && !_blocker.CanInteract(user, user) ||
            !TryComp<NCMedicalImplantComponent>(uid, out var medical) || !medical.Injector ||
            !IsReady(uid, out var body) || user != body || !TryComp<BloodstreamComponent>(body, out var blood)) return false;
        var state = medical.State;
        if (!NCMedicalImplantMath.Valid(state) || automatic &&
            (!state.Automatic || !TryGetReading(body, out var reading) || !Triggered(state, reading))) return false;
        // Low-grade pumps have coarser metering. The actual amount, not the requested amount, consumes the budget.
        var quality = _cyberware.MedicalPartQuality(uid, "NCMedicalPumpSlot");
        var dose = NCMedicalImplantMath.MeteredDose(state.Dose, quality.MeasurementStep);
        if (!NCMedicalImplantMath.CanDose(dose, state.Delivered, state.Limit, state.CooldownSeconds, state.Cartridge.Volume.Float())) return false;
        if (!_cyberware.TryBeginAbilityLoad(uid, 1 / quality.Performance)) return false;
        _cyberware.TrySetLoad(uid, 0);
        var injection = state.Cartridge.SplitSolution(FixedPoint2.New(dose));
        if (!_blood.TryAddToBloodstream((body, blood), injection))
        { state.Cartridge.AddSolution(injection, _prototypes); return false; }
        state.Delivered += dose;
        state.CooldownSeconds = state.IntervalSeconds;
        Changed(body);
        _popup.PopupEntity(Loc.GetString("nc-medical-implant-injected", ("dose", dose)), body, body);
        return true;
    }

    public bool TryRead(EntityUid body)
    {
        if (!TryGetReading(body, out var reading)) return false;
        ShowReading(body, reading);
        return true;
    }

    private void ShowReading(EntityUid body, NCMedicalImplantComponent reading) =>
        _popup.PopupEntity(Loc.GetString("nc-medical-implant-reading", ("oxygen", reading.Oxygen),
            ("perfusion", reading.Perfusion), ("pain", reading.Pain), ("blood", reading.Blood)), body, body, PopupType.Large);

    private bool CanManage(EntityUid uid, EntityUid user) =>
        _blocker.CanInteract(user, user) && _cyberware.TryGetOwningBody(uid, out var body) && body == user;

    private bool TryLoad(EntityUid uid, EntityUid user)
    {
        if (!CanManage(uid, user) || !TryComp<NCMedicalImplantComponent>(uid, out var module) || !module.Injector ||
            module.State.Cartridge.Volume > 0 || _hands.GetActiveItem(user) is not { } held ||
            !HasComp<NCMedicalCartridgeComponent>(held) || !_solutions.TryGetSolution(held, "cartridge", out var solution, out var contents) ||
            contents.Volume <= 0 || contents.Volume > 30) return false;
        module.State.Cartridge = _solutions.SplitSolution(solution.Value, contents.Volume);
        module.State.Delivered = 0;
        module.State.Automatic = false;
        Changed(user);
        return true;
    }

    private bool TryUnload(EntityUid uid, EntityUid user)
    {
        if (!CanManage(uid, user) || !TryComp<NCMedicalImplantComponent>(uid, out var module) || !module.Injector ||
            _hands.GetActiveItem(user) is not { } held || !HasComp<NCMedicalCartridgeComponent>(held) ||
            !_solutions.TryGetSolution(held, "cartridge", out var solution, out var contents) || contents.Volume != 0) return false;
        if (!_solutions.TryAddSolution(solution.Value, module.State.Cartridge)) return false;
        module.State.Cartridge = new() { MaxVolume = 30 };
        module.State.Automatic = false;
        Changed(user);
        return true;
    }

    public void AddPatientVerbs(Entity<NCMedicalBodyComponent> body, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User != body.Owner || !args.CanInteract || !args.CanAccess) return;
        var user = args.User;
        foreach (var uid in _cyberware.GetInstalledModuleEntities(user, 32))
        {
            if (!TryComp<NCMedicalImplantComponent>(uid, out var module)) continue;
            var verbs = args.Verbs;
            void Add(string key, Action action) => verbs.Add(new AlternativeVerb
            { Text = Loc.GetString(key), Act = () => { if (CanManage(uid, user)) action(); } });
            if (!module.Injector)
            {
                Add("nc-medical-implant-read", () => { if (TryGetReading(user, out var reading)) ShowReading(user, reading); });
                continue;
            }
            Add("nc-medical-implant-dose", () => { if (!TryInject(uid, user)) _popup.PopupEntity(Loc.GetString("nc-medical-implant-blocked"), user, user); });
            Add("nc-medical-implant-load", () => TryLoad(uid, user));
            Add("nc-medical-implant-unload", () => TryUnload(uid, user));
            Add("nc-medical-implant-auto", () => { module.State.Automatic = !module.State.Automatic && TryGetReading(user, out _); ShowSettings(uid, user, module); });
            Add("nc-medical-implant-dose-setting", () => { module.State.Automatic = false; module.State.Dose = module.State.Dose >= 5 ? 1 : module.State.Dose + 1; ShowSettings(uid, user, module); });
            Add("nc-medical-implant-interval-setting", () => { module.State.Automatic = false; module.State.IntervalSeconds = module.State.IntervalSeconds >= 300 ? 30 : module.State.IntervalSeconds + 30; ShowSettings(uid, user, module); });
            Add("nc-medical-implant-limit-setting", () => { module.State.Automatic = false; module.State.Limit = module.State.Limit >= 30 ? 5 : module.State.Limit + 5; ShowSettings(uid, user, module); });
            Add("nc-medical-implant-trigger-setting", () => { module.State.Automatic = false; module.State.Trigger = (NCMedicalTrigger) (((int) module.State.Trigger + 1) % 3); ShowSettings(uid, user, module); });
            Add("nc-medical-implant-threshold-setting", () => { module.State.Automatic = false; module.State.Threshold = module.State.Threshold >= 90 ? 10 : module.State.Threshold + 10; ShowSettings(uid, user, module); });
            Add("nc-medical-implant-settings", () => ShowSettings(uid, user, module));
        }
    }

    private void ShowSettings(EntityUid uid, EntityUid body, NCMedicalImplantComponent module)
    {
        Changed(body);
        _popup.PopupEntity(Settings(uid, module), body, body, PopupType.Large);
    }

    private void Changed(EntityUid body) => _medical.TryMarkDirty(body, NCMedicalDirtyDomains.Persistence | NCMedicalDirtyDomains.Diagnostics);

    public string Settings(EntityUid uid, NCMedicalImplantComponent module) => Loc.GetString("nc-medical-implant-settings-text",
        ("dose", NCMedicalImplantMath.MeteredDose(module.State.Dose, _cyberware.MedicalPartQuality(uid, "NCMedicalPumpSlot").MeasurementStep)), ("requested", module.State.Dose), ("interval", module.State.IntervalSeconds), ("limit", module.State.Limit),
        ("delivered", module.State.Delivered), ("remaining", module.State.Cartridge.Volume),
        ("cooldown", MathF.Ceiling(module.State.CooldownSeconds)),
        ("automatic", Loc.GetString(module.State.Automatic ? "nc-medical-implant-on" : "nc-medical-implant-off")),
        ("trigger", Loc.GetString($"nc-medical-trigger-{(int) module.State.Trigger}")), ("threshold", module.State.Threshold));

    private void OnExamined(Entity<NCMedicalImplantComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.Injector) args.PushMarkup(Settings(ent.Owner, ent.Comp));
        args.PushMarkup(Loc.GetString(_cyberware.MedicalControllerReady(ent) ? "nc-medical-controller-ready" : "nc-medical-controller-unready"));
    }

    private void OnCyberwareExamined(Entity<NCCyberwareComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("nc-cyberware-frame-quality", ("quality", Loc.GetString(_prototypes.Index(ent.Comp.Quality).Name))));
        if (_cyberware.GetAssemblyQuality(ent) is { } grade)
            args.PushMarkup(Loc.GetString("nc-medical-part-quality", ("quality", Loc.GetString(_prototypes.Index(grade).Name))));
    }

    private void OnPartExamined(Entity<NCCyberwarePartComponent> ent, ref ExaminedEvent args)
    {
        var quality = _prototypes.Index(_prototypes.Index(ent.Comp.Profile).Quality);
        args.PushMarkup(Loc.GetString("nc-medical-part-quality", ("quality", Loc.GetString(quality.Name))));
        if (TryComp<NCMedicalControllerComponent>(ent, out var controller))
            args.PushMarkup(Loc.GetString("nc-medical-controller-firmware", ("firmware", controller.Firmware is { } firmware
                ? Loc.GetString(_prototypes.Index(firmware).Name) : Loc.GetString("nc-medical-firmware-blank"))));
    }
}
