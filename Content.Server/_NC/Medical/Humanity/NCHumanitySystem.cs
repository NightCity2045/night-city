// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Borg;
using Content.Shared._NC.Medical.Humanity;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.Examine;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Humanity;

/// <summary>One brain owns H/S and adaptation. Physical graph owners call reconciliation only after committing.</summary>
public sealed partial class NCHumanitySystem : EntitySystem
{
    private static readonly ProtoId<NCHumanityProfilePrototype> DefaultProfile = "NCHumanityDefault";
    private static readonly ProtoId<NCHumanityStimulusPrototype> NeuralTrauma = "NCHumanityNeuralTrauma";
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCMedicalBodySystem _medical = default!;
    [Dependency] private NCPhysiologySchedulerSystem _scheduler = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    private readonly HashSet<EntityUid> _pending = [];
    private readonly HashSet<EntityUid> _restoring = [];
    private bool _profileValidated;

    private NCHumanityProfilePrototype GetProfile()
    {
        var profile = _prototypes.Index(DefaultProfile);
        if (!_profileValidated && !NCHumanityMath.ValidateProfile(profile))
            throw new InvalidOperationException($"Invalid Humanity profile {profile.ID}.");
        _profileValidated = true;
        return profile;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanityMonitorComponent, MapInitEvent>(OnMapInit,
            after: [typeof(InitialBodySystem), typeof(NCCyberwareSystem)]);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCMedicalBodyChangedEvent>(OnBodyChanged);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCMedicalIntervalCompletedEvent>(OnIntervalCompleted);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCMedicalNextUpdateEvent>(OnNextUpdate);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCCyberwareInstalledEvent>(OnInstalled);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCCyberwareRemovedEvent>(OnRemoved);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCCyberwareStateChangedEvent>(OnCyberwareChanged);
        SubscribeLocalEvent<NCHumanityMonitorComponent, GetVerbsEvent<InteractionVerb>>(OnGetVerbs);
        SubscribeLocalEvent<NCHumanityMonitorComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<NCHumanityMonitorComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnMapInit(Entity<NCHumanityMonitorComponent> body, ref MapInitEvent args) => TryReconcile(body);
    private void OnShutdown(Entity<NCHumanityMonitorComponent> body, ref ComponentShutdown args)
    { _pending.Remove(body); _restoring.Remove(body); }
    private void OnBodyChanged(Entity<NCHumanityMonitorComponent> body, ref NCMedicalBodyChangedEvent args)
    {
        if ((args.Domains & NCMedicalDirtyDomains.Anatomy) != 0 && !_restoring.Contains(body))
            _pending.Add(body);
    }
    private void OnInstalled(Entity<NCHumanityMonitorComponent> body, ref NCCyberwareInstalledEvent args) => TryReconcile(body);
    private void OnRemoved(Entity<NCHumanityMonitorComponent> body, ref NCCyberwareRemovedEvent args)
    {
        if (TryComp<NCCyberwareComponent>(args.Module, out var module))
        { module.CrisisEfficiency = 1f; module.CrisisBlocksActiveModes = false; }
        TryReconcile(body);
    }
    private void OnCyberwareChanged(Entity<NCHumanityMonitorComponent> body, ref NCCyberwareStateChangedEvent args)
    {
        if (args.Kind == NCCyberwareStateChangeKind.Configuration)
            TryReconcile(body);
        else if (args.Kind is NCCyberwareStateChangeKind.Emp or NCCyberwareStateChangeKind.MechanicalTrauma &&
                 TryComp<NCCyberwareComponent>(args.Module, out var module) &&
                 module.NeuralIntegrity < GetProfile().NeuralTraumaThreshold)
            TryApplyStimulus(body, NeuralTrauma, args.ResolutionId.Value);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_pending.Count == 0) return;
        var pending = _pending.ToArray();
        _pending.Clear();
        foreach (var body in pending)
        {
            if (!TerminatingOrDeleted(body)) TryReconcile(body);
        }
    }

    private bool TryGetBrain(EntityUid body, out Entity<NCHumanityComponent> brain)
    {
        brain = default;
        if (!Exists(body) || !HasComp<NCMedicalBodyComponent>(body) ||
            !_anatomy.TryGetOrgan(body, "Brain", out var organ) || !HasComp<BrainComponent>(organ))
            return false;
        if (TryComp<NCHumanityComponent>(organ, out var existing)) brain = (organ, existing);
        else if (!TerminatingOrDeleted(body) && !TerminatingOrDeleted(organ)) brain = (organ, EnsureComp<NCHumanityComponent>(organ));
        else return false;
        return true;
    }

    /// <summary>Settle the old configuration before its physical owner changes it; never during graph rollback.</summary>
    public void TrySettleBody(EntityUid body)
    {
        if (_restoring.Contains(body) || !TryComp<NCCirculationComponent>(body, out var circulation)) return;
        var elapsed = _timing.CurTime - circulation.LastEvaluation;
        if (elapsed > TimeSpan.Zero) _scheduler.ProcessNow(body, elapsed);
    }

    public bool TryReconcile(EntityUid body, bool creditExisting = false)
    {
        if (_restoring.Contains(body) || !TryGetBrain(body, out var brain)) return false;
        var packages = new Dictionary<string, NCHumanityPackageDose>(StringComparer.Ordinal);
        if (!TryCollectPackages(body, packages, out var modules)) return false;
        var state = brain.Comp.State;
        var before = (state.Current, state.Burden);
        if (!NCHumanityMath.TryReconcile(state, packages, creditExisting)) return false;
        ApplyEnhancementRestrictions(modules, state.Stage);
        if (before != (state.Current, state.Burden)) MarkChanged(body);
        return true;
    }

    private bool TryCollectPackages(EntityUid body, Dictionary<string, NCHumanityPackageDose> packages,
        out List<(EntityUid Uid, bool Affected)> modules)
    {
        modules = [];
        if (!TryComp<BodyComponent>(body, out var graph) || graph.Organs == null) return false;
        if (TryComp<NCBorgCoreComponent>(body, out var core) &&
            !TryAddPackages(core.HumanityPackages, "core", packages, out _)) return false;
        foreach (var organ in graph.Organs.ContainedEntities)
        {
            if (!TryComp<OrganComponent>(organ, out var node) || node.Category is not { } category) continue;
            if (!TryCollectModule(organ, category.Id, packages, modules)) return false;
            if (!TryComp<NCCyberwareSlotHostComponent>(organ, out var host) || host.Modules == null) continue;
            foreach (var module in host.Modules.ContainedEntities)
                if (!TryCollectModule(module, category.Id, packages, modules)) return false;
        }
        return packages.Count <= 256;
    }

    private bool TryCollectModule(EntityUid uid, string context, Dictionary<string, NCHumanityPackageDose> packages,
        List<(EntityUid Uid, bool Affected)> modules)
    {
        if (!TryComp<NCCyberwareComponent>(uid, out var module)) return true;
        if (!_prototypes.TryIndex(module.Profile, out var definition) ||
            !TryAddPackages(definition.HumanityPackages, context, packages, out var affected)) return false;
        if (TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly))
        {
            foreach (var slot in assembly.Parts.Values)
            {
                if (slot.ContainedEntity is not { } part || !TryComp<NCCyberwarePartComponent>(part, out var hardware) ||
                    !_prototypes.TryIndex(hardware.Profile, out var partDefinition)) continue;
                if (!TryAddPackages(partDefinition.HumanityPackages, context, packages, out var partAffected)) return false;
                affected |= partAffected;
            }
        }
        modules.Add((uid, affected));
        return true;
    }

    private bool TryAddPackages(IEnumerable<ProtoId<NCHumanityPackagePrototype>> ids, string context,
        Dictionary<string, NCHumanityPackageDose> packages, out bool affected)
    {
        affected = false;
        foreach (var id in ids)
        {
            if (!_prototypes.TryIndex(id, out var package) || string.IsNullOrWhiteSpace(package.AdaptationKey) ||
                package.AdaptationKey.Length > 128 || !float.IsFinite(package.Burden) || package.Burden is < 0f or > 100f ||
                !float.IsFinite(package.AcuteCost) || package.AcuteCost is < 0f or > 100f) return false;
            if (package.Burden == 0f && package.AcuteCost == 0f && !package.CrisisAffected)
                continue;
            var key = package.AdaptationKey + (package.PerAnatomicalContext ? ":" + context : string.Empty);
            var existing = packages.GetValueOrDefault(key);
            packages[key] = new(MathF.Max(existing.Burden, package.Burden), MathF.Max(existing.AcuteCost, package.AcuteCost));
            affected |= package.CrisisAffected;
        }
        return true;
    }

    private void OnNextUpdate(Entity<NCHumanityMonitorComponent> body, ref NCMedicalNextUpdateEvent args)
    {
        if (!TryGetBrain(body, out var brain) || brain.Comp.State.Stress <= 0f &&
            brain.Comp.State.Burden <= 0f && brain.Comp.State.Stage == NCCyberCrisisStage.Stable &&
            !brain.Comp.State.RecentImpulses.Any(memory => memory.RemainingSeconds > 0f))
            return;
        var interval = GetProfile().ActiveUpdateInterval;
        if (args.Delay == null || args.Delay > interval) args.Delay = interval;
    }

    private void OnIntervalCompleted(Entity<NCHumanityMonitorComponent> body, ref NCMedicalIntervalCompletedEvent args)
        => TryAdvanceBody(body, args.Elapsed);

    public bool TryAdvanceBody(EntityUid body, TimeSpan elapsed)
    {
        if (_restoring.Contains(body) || elapsed < TimeSpan.Zero || !TryGetBrain(body, out var brain) ||
            !TryComp<NCPhysiologyComponent>(body, out var physiology) || physiology.Irreversible ||
            !_prototypes.TryIndex(DefaultProfile, out var profile) || !NCHumanityMath.ValidateProfile(profile)) return false;
        var seconds = (float) elapsed.TotalSeconds;
        if (!float.IsFinite(seconds) || seconds > 86400f) return false;
        var state = brain.Comp.State;
        if (!NCHumanityMath.Validate(state)) return false;
        var drugs = _pharmacology.GetModifiers(body);
        if (state.Burden <= 0f && state.Stress <= 0f && state.Stage == NCCyberCrisisStage.Stable &&
            !state.RecentImpulses.Any(memory => memory.RemainingSeconds > 0f) &&
            (state.Current >= NCHumanityMath.Base || drugs.HumanityRecoveryPerSecond <= 0f))
            return true;
        var packages = new Dictionary<string, NCHumanityPackageDose>(StringComparer.Ordinal);
        if (!TryCollectPackages(body, packages, out var modules)) return false;
        var overloaded = false;
        foreach (var (uid, affected) in modules)
        {
            if (affected && TryComp<NCCyberwareComponent>(uid, out var module) && module.Operational &&
                module.Load > 0f && _prototypes.TryIndex(module.Profile, out var hardware) &&
                module.Heat > hardware.HeatDeratingThreshold) overloaded = true;
        }
        var stable = TryComp<NCCirculationComponent>(body, out var circulation) &&
                     circulation.Perfusion >= profile.StableMinimumPerfusion &&
                     physiology.Oxygenation >= profile.StableMinimumOxygenation && physiology.Shock <= profile.StableMaximumShock;
        var before = (state.Current, state.Stress, state.Stage, state.StabilizingSeconds);
        var cooldownAdvanced = seconds > 0f && state.RecentImpulses.Any(memory => memory.RemainingSeconds > 0f);
        NCHumanityMath.Advance(state, profile, seconds, drugs.HumanityRecoveryPerSecond,
            drugs.CrisisSuppressionPerSecond, overloaded, stable);
        ApplyEnhancementRestrictions(modules, state.Stage);
        if (before != (state.Current, state.Stress, state.Stage, state.StabilizingSeconds) || cooldownAdvanced)
        {
            MarkChanged(body, before.Stage != state.Stage);
            if (before.Stage != state.Stage)
                _popup.PopupEntity(Loc.GetString(StageMessage(state.Stage)), body, body, PopupType.LargeCaution);
        }
        return true;
    }

    public bool TryApplyStimulus(EntityUid body, ProtoId<NCHumanityStimulusPrototype> id, Guid source)
    {
        if (source == Guid.Empty || !TryGetBrain(body, out var brain) || _restoring.Contains(body) ||
            !_prototypes.TryIndex(id, out var stimulus) || !float.IsFinite(stimulus.Impulse) || stimulus.Impulse < 0f ||
            !float.IsFinite(stimulus.CooldownSeconds) || stimulus.CooldownSeconds is <= 0f or > 86400f) return false;
        var state = brain.Comp.State;
        if (state.Burden <= 0f || state.RecentImpulses.Any(memory => memory.Source == source ||
                memory.Profile == id && memory.RemainingSeconds > 0f)) return false;
        state.RecentImpulses.RemoveAll(memory => memory.RemainingSeconds <= 0f);
        if (state.RecentImpulses.Count >= NCHumanityMath.MaximumRecentImpulses) return false;
        state.RecentImpulses.Add(new() { Source = source, Profile = id, RemainingSeconds = stimulus.CooldownSeconds });
        var profile = GetProfile();
        var previousStage = state.Stage;
        state.Stress = Math.Clamp(state.Stress + stimulus.Impulse *
            (1f + profile.VulnerabilityScale * (1f - state.Current / NCHumanityMath.Base)), 0f, 100f);
        NCHumanityMath.UpdateStage(state, profile, 0f);
        MarkChanged(body, previousStage != state.Stage);
        if (TryCollectPackages(body, new(StringComparer.Ordinal), out var modules))
            ApplyEnhancementRestrictions(modules, state.Stage);
        if (previousStage != state.Stage)
            _popup.PopupEntity(Loc.GetString(StageMessage(state.Stage)), body, body, PopupType.LargeCaution);
        _scheduler.Schedule(body);
        return true;
    }

    private void ApplyEnhancementRestrictions(List<(EntityUid Uid, bool Affected)> modules, NCCyberCrisisStage stage)
    {
        var factor = EnhancementEfficiency(stage);
        var blocks = GetProfile().Bands.Any(band => band.Stage == stage && band.BlocksEnhancedModes);
        foreach (var (uid, affected) in modules)
            if (TryComp<NCCyberwareComponent>(uid, out var module))
            { module.CrisisEfficiency = affected ? factor : 1f; module.CrisisBlocksActiveModes = affected && blocks; }
    }
    public float EnhancementEfficiency(NCCyberCrisisStage stage)
    {
        foreach (var band in GetProfile().Bands)
            if (band.Stage == stage) return band.EnhancementEfficiency;
        return 1f;
    }

    public bool TryGetStatus(EntityUid body, out NCHumanityStatus status)
    {
        status = default;
        if (!TryGetBrain(body, out var brain) || !NCHumanityMath.Validate(brain.Comp.State)) return false;
        var state = brain.Comp.State;
        status = new(state.Current, NCHumanityMath.Maximum(state.Burden), state.Burden, state.Stress,
            state.Stage, EnhancementEfficiency(state.Stage));
        return true;
    }
    private void MarkChanged(EntityUid body, bool mechanical = false) => _medical.TryMarkDirty(body,
        NCMedicalDirtyDomains.Diagnostics | NCMedicalDirtyDomains.Persistence |
        (mechanical ? NCMedicalDirtyDomains.OrganFunction : NCMedicalDirtyDomains.None));
    public static string StageMessage(NCCyberCrisisStage stage) => stage switch
    {
        NCCyberCrisisStage.Warning => "nc-humanity-stage-warning",
        NCCyberCrisisStage.Crisis => "nc-humanity-stage-crisis",
        NCCyberCrisisStage.Acute => "nc-humanity-stage-acute",
        _ => "nc-humanity-stage-stable",
    };
    private string StatusText(NCHumanityStatus status) => Loc.GetString("nc-humanity-status",
        ("current", MathF.Round(status.Current, 1)), ("maximum", MathF.Round(status.Maximum, 1)),
        ("burden", MathF.Round(status.Burden, 1)), ("stress", MathF.Round(status.Stress, 1)),
        ("stage", Loc.GetString(StageMessage(status.Stage))));
    private void OnGetVerbs(Entity<NCHumanityMonitorComponent> body, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (args.User != body.Owner || !TryGetStatus(body, out _)) return;
        var uid = body.Owner;
        args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-humanity-check"),
            Act = () => { if (TryGetStatus(uid, out var status)) _popup.PopupEntity(StatusText(status), uid, uid, PopupType.Large); } });
    }
    private void OnExamined(Entity<NCHumanityMonitorComponent> body, ref ExaminedEvent args)
    { if (args.Examiner == body.Owner && TryGetStatus(body, out var status)) args.PushMarkup(StatusText(status)); }

    public bool TryCapturePersistentState(EntityUid body, out NCHumanityStateSnapshot? snapshot)
    {
        snapshot = null;
        if (!TryGetBrain(body, out var brain)) return true;
        if (!NCHumanityMath.Validate(brain.Comp.State)) return false;
        snapshot = NCHumanityMath.Clone(brain.Comp.State);
        return true;
    }
    public bool TryRestorePersistentState(EntityUid body, NCHumanityStateSnapshot? state)
    {
        if (state != null && !NCHumanityMath.Validate(state)) return false;
        if (!TryGetBrain(body, out var brain)) return state == null;
        if (state != null) brain.Comp.State = NCHumanityMath.Clone(state);
        // A legacy snapshot has no psychological data: adopt its existing configuration once, without a new acute bill.
        _restoring.Remove(body);
        return TryReconcile(body, state == null);
    }
    public IDisposable SuspendConfiguration(EntityUid body)
    { _restoring.Add(body); return new RestoreScope(_restoring, body); }
    private sealed class RestoreScope(HashSet<EntityUid> restoring, EntityUid body) : IDisposable
    { public void Dispose() => restoring.Remove(body); }
}
