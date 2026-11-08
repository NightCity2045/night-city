// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Humanity;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization;

namespace Content.Server._NC.Medical.Humanity;

public sealed partial class NCHumanitySystem
{
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private NCCyberwareAbilitySystem _abilities = default!;
    [Dependency] private Content.Server._NC.Medical.Cyberware.NCCyberwareSystem _cyberware = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    private TimeSpan _nextPresentation;
    private readonly Dictionary<EntityUid, EpisodePresentation> _presentations = [];

    private sealed class EpisodePresentation
    {
        public EntityUid Brain;
        public uint Sequence;
        public ProtoId<NCCyberSymptomPrototype>? Symptom;
        public EntityUid? PendingModule;
        public TimeSpan MotorAt;
        public TimeSpan LastAction;
        public NCCyberCrisisStage Stage;
    }

    private void InitializeEpisodes()
    {
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCWoundImpactAppliedEvent>(OnPsychologicalImpact);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCTraumaticAmputationEvent>(OnPsychologicalAmputation);
        SubscribeLocalEvent<NCHumanityMonitorComponent, MobStateChangedEvent>(OnWitnessDeath);
        SubscribeLocalEvent<NCHumanityMonitorComponent, NCGroundingDoAfterEvent>(OnGroundingComplete);
        SubscribeNetworkEvent<NCCyberEpisodeActionEvent>(OnEpisodeAction);
    }

    private void OnPsychologicalImpact(Entity<NCHumanityMonitorComponent> body, ref NCWoundImpactAppliedEvent args)
    {
        var id = args.Request.ResolutionId.Value;
        TryApplyStimulus(body, args.Severity >= NCWoundSeverity.Severe ? "NCHumanitySevereTrauma" : "NCHumanityCombat", id);
        if (args.Request.Instigator is { } attacker && attacker != body.Owner &&
            _interaction.InRangeUnobstructed(attacker, body.Owner, 8f))
            TryApplyStimulus(attacker, "NCHumanityViolence", id);
    }

    private void OnPsychologicalAmputation(Entity<NCHumanityMonitorComponent> body, ref NCTraumaticAmputationEvent args)
        => TryApplyStimulus(body, "NCHumanityAmputation", args.SourceRequest.ResolutionId.Value);

    private void OnWitnessDeath(Entity<NCHumanityMonitorComponent> body, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead) return;
        var source = Guid.NewGuid();
        foreach (var witness in _lookup.GetEntitiesInRange<NCHumanityMonitorComponent>(Transform(body).Coordinates, 6f))
        {
            if (witness.Owner == body.Owner || !IsAlert(witness) || !_interaction.InRangeUnobstructed(witness.Owner, body.Owner, 6f)) continue;
            TryApplyStimulus(witness, "NCHumanityWitnessDeath", source);
        }
    }

    private bool IsAlert(EntityUid body) => !HasComp<Content.Shared.Bed.Sleep.SleepingComponent>(body) && TryComp<NCPhysiologyComponent>(body, out var physiology) &&
        !physiology.Irreversible && physiology.Consciousness < NCConsciousnessState.Unconscious &&
        (!TryComp<MobStateComponent>(body, out var mob) || mob.CurrentState == MobState.Alive);

    private void AddPhysiologicalStrain(EntityUid body, NCHumanityStateSnapshot state, NCPhysiologyComponent physiology,
        NCPharmacologyModifiers drugs, List<(EntityUid Uid, bool Affected)> modules, float seconds, ref bool stable)
    {
        if (!GetProfile().EpisodesEnabled || state.Burden <= 0f) return;
        var pain = Math.Clamp((physiology.TotalPain - drugs.Analgesia - 10f) / 100f, 0f, .2f);
        var strain = pain + Math.Clamp(physiology.Shock - .2f, 0f, .25f) + drugs.OverdoseFraction * .3f;
        foreach (var (uid, affected) in modules)
            if (affected && TryComp<NCCyberwareComponent>(uid, out var module) && module.Load > 0f)
                strain += .025f * module.Load;
        strain = Math.Min(strain, .65f);
        if (strain > .02f) stable = false;
        state.Stress = Math.Clamp(state.Stress + strain * seconds * (1f + (100f - state.Current) / 100f), 0f, 100f);
    }

    private void UpdateEpisodePresentation()
    {
        if (_timing.CurTime < _nextPresentation) return;
        _nextPresentation = _timing.CurTime + TimeSpan.FromSeconds(1);
        // Only actual connected owners receive symptoms. No per-frame scan of the city's population.
        var query = EntityQueryEnumerator<NCHumanityMonitorComponent, ActorComponent>();
        var active = new HashSet<EntityUid>();
        while (query.MoveNext(out var body, out _, out var actor))
        {
            if (!TryGetBrain(body, out var brain) || !TryGetStatus(body, out var status) || !IsAlert(body)) continue;
            active.Add(body);
            if (!_presentations.TryGetValue(body, out var presentation) || presentation.Brain != brain.Owner)
                _presentations[body] = presentation = new EpisodePresentation { Brain = brain.Owner };
            var state = brain.Comp.State;
            if (presentation.Stage != state.Stage)
            {
                presentation.Stage = state.Stage;
                presentation.Symptom = null;
                presentation.PendingModule = null;
                presentation.Sequence++;
                Log.Info($"Cyber episode {ToPrettyString(body)}: {state.Stage}; strain {state.Stress:F1}, exhaustion {state.Exhaustion:F1}");
            }
            if (state.Stage == NCCyberCrisisStage.Stable)
            {
                presentation.Symptom = null;
                presentation.PendingModule = null;
            }
            else if (state.Stage == NCCyberCrisisStage.Recovery)
            {
                presentation.PendingModule = null;
                if (state.SymptomSeconds <= 0f)
                {
                    presentation.Symptom = "NCCyberAfterimage";
                    presentation.Sequence++;
                    state.SymptomSeconds = 25f;
                    MarkChanged(body);
                }
            }
            else
            {
                if (state.Episode == null || !_prototypes.HasIndex(state.Episode.Value))
                    state.Episode = SelectEpisode(body);
                if (state.SymptomSeconds <= 0f)
                    SelectSymptom(body, state, presentation);
                if (presentation.PendingModule is { } module && _timing.CurTime >= presentation.MotorAt)
                {
                    presentation.PendingModule = null;
                    // Native activation still checks power, calibration, hands and hardware. Never attacks.
                    if (state.Stage == NCCyberCrisisStage.Crisis && state.SupportSeconds <= 0f && state.MotorInhibitSeconds <= 0f &&
                        TryComp<NCCyberwareAbilityComponent>(module, out var ability) && !ability.Active)
                        _abilities.TryToggle(body, module);
                }
            }
            NetEntity? perceivedTarget = null;
            if (presentation.Symptom is { } symptomId && _prototypes.Index(symptomId).Threat)
                foreach (var candidate in _lookup.GetEntitiesInRange<NCHumanityMonitorComponent>(Transform(body).Coordinates, 5f))
                {
                    if (candidate.Owner == body || !IsAlert(candidate) || !_interaction.InRangeUnobstructed(body, candidate.Owner, 5f)) continue;
                    perceivedTarget = GetNetEntity(candidate);
                    break;
                }
            RaiseNetworkEvent(new NCCyberEpisodeViewEvent
            {
                Body = GetNetEntity(body), PerceivedTarget = perceivedTarget, Status = status, Episode = state.Episode,
                Symptom = presentation.Symptom, Sequence = presentation.Sequence, DeclinesSupport = state.DeclinesSupport,
            }, actor.PlayerSession.Channel);
        }
        foreach (var body in _presentations.Keys.Where(body => !active.Contains(body)).ToArray()) _presentations.Remove(body);
    }

    private ProtoId<NCCyberEpisodePrototype> SelectEpisode(EntityUid body)
    {
        var packages = new Dictionary<string, NCHumanityPackageDose>();
        TryCollectPackages(body, packages, out _);
        var choices = _prototypes.EnumeratePrototypes<NCCyberEpisodePrototype>()
            .Where(p => p.PackageKey.Length > 0 && packages.Keys.Any(k => k.StartsWith(p.PackageKey, StringComparison.Ordinal)))
            .OrderBy(p => p.ID).ToArray();
        return choices.Length == 0 ? "NCCyberHypervigilance" : choices[_random.Next(choices.Length)].ID;
    }

    private void SelectSymptom(EntityUid body, NCHumanityStateSnapshot state, EpisodePresentation presentation)
    {
        var episode = _prototypes.Index(state.Episode!.Value);
        var choices = episode.Symptoms.Select(id => _prototypes.Index(id))
            .Where(s => s.MinimumStage <= state.Stage && (presentation.Symptom == null || s.ID != presentation.Symptom.Value.Id)).ToArray();
        if (choices.Length == 0) return;
        var symptom = choices[_random.Next(choices.Length)];
        presentation.Symptom = symptom.ID;
        presentation.Sequence++;
        state.SymptomSeconds = Math.Max(symptom.Duration + 2f, symptom.Cooldown);
        if (symptom.MotorWarning && state.Stage == NCCyberCrisisStage.Crisis && state.SupportSeconds <= 0f && state.MotorInhibitSeconds <= 0f &&
            TryComp<NCCyberwareAbilityBodyComponent>(body, out var controls))
        {
            foreach (var module in controls.Modules)
                if (TryComp<NCCyberwareAbilityComponent>(module, out var ability) && !ability.Active &&
                    _prototypes.Index(ability.Profile).Kind == NCCyberwareAbilityKind.Blade)
                {
                    presentation.PendingModule = module;
                    presentation.MotorAt = _timing.CurTime + TimeSpan.FromSeconds(4);
                    break;
                }
        }
        MarkChanged(body);
    }

    private void AddEpisodeVerbs(Entity<NCHumanityMonitorComponent> body, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !TryGetBrain(body, out var brain)) return;
        var patient = body.Owner;
        var helper = args.User;
        if (helper == patient)
        {
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-psychosis-panel"), Act = () =>
                { if (TryComp<ActorComponent>(patient, out var actor)) RaiseNetworkEvent(new NCCyberEpisodeOpenEvent(), actor.PlayerSession.Channel); } });
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-psychosis-safe-mode"), Act = () => TryDisableEnhancements(patient) });
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString(brain.Comp.State.DeclinesSupport ? "nc-psychosis-allow-support" : "nc-psychosis-decline-support"),
                Act = () => { if (TryGetBrain(patient, out var current)) { current.Comp.State.DeclinesSupport = !current.Comp.State.DeclinesSupport; MarkChanged(patient); } } });
        }
        else if (CanGround(patient, helper))
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-psychosis-ground"), Act = () => TryGround(patient, helper) });
    }

    private bool CanGround(EntityUid patient, EntityUid helper) => patient != helper && IsAlert(patient) && IsAlert(helper) &&
        _interaction.InRangeUnobstructed(helper, patient, 1.5f) && TryGetBrain(patient, out var brain) &&
        !brain.Comp.State.DeclinesSupport && brain.Comp.State.SupportCooldown <= 0f;

    public bool TryGround(EntityUid patient, EntityUid helper)
    {
        if (!CanGround(patient, helper) || !TryGetBrain(patient, out var brain)) return false;
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, helper, TimeSpan.FromSeconds(8),
            new NCGroundingDoAfterEvent { Brain = GetNetEntity(brain) }, patient, target: patient)
            { BreakOnMove = true, BreakOnDamage = true, DistanceThreshold = 1.5f, NeedHand = false });
    }

    private void OnGroundingComplete(Entity<NCHumanityMonitorComponent> patient, ref NCGroundingDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || !CanGround(patient, args.User) || !TryGetBrain(patient, out var brain) || GetNetEntity(brain) != args.Brain) return;
        args.Handled = true;
        brain.Comp.State.SupportSeconds = 30f;
        brain.Comp.State.SupportCooldown = 60f;
        brain.Comp.State.Stress = Math.Max(0f, brain.Comp.State.Stress - 5f);
        MarkChanged(patient);
        _popup.PopupEntity(Loc.GetString("nc-psychosis-grounded"), patient, patient, PopupType.Medium);
        _scheduler.Schedule(patient);
    }

    public bool TryDisableEnhancements(EntityUid body)
    {
        if (!IsAlert(body) || !TryGetBrain(body, out _) || !TryCollectPackages(body, new(), out var modules)) return false;
        foreach (var (module, affected) in modules)
        {
            if (!affected) continue;
            if (HasComp<NCCyberwareAbilityComponent>(module)) _abilities.TrySetEnabled(body, module, false);
            _cyberware.TrySetLoad(module, 0f);
        }
        if (_presentations.TryGetValue(body, out var presentation)) presentation.PendingModule = null;
        // Acknowledged safe mode prevents a pending reflex from immediately reopening a blade.
        if (TryGetBrain(body, out var brain)) brain.Comp.State.MotorInhibitSeconds = 30f;
        MarkChanged(body, true);
        return true;
    }

    private void OnEpisodeAction(NCCyberEpisodeActionEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } body || !IsAlert(body) || !_presentations.TryGetValue(body, out var presentation) ||
            _timing.CurTime < presentation.LastAction + TimeSpan.FromSeconds(1)) return;
        presentation.LastAction = _timing.CurTime;
        if (args.DisableEnhancements) TryDisableEnhancements(body);
        if (args.ToggleSupport && TryGetBrain(body, out var brain))
        { brain.Comp.State.DeclinesSupport = !brain.Comp.State.DeclinesSupport; MarkChanged(body); }
    }
}
