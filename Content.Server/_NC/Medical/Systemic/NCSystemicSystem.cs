// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Systemic;

/// <summary>
/// Owns bounded whole-body burdens produced by atmosphere, reagents, radiation, cellular effects and
/// electrical exposure. Work is event-driven or invoked for one body by the central physiology scheduler.
/// </summary>
public sealed partial class NCSystemicSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, NCSystemicExposureEvent>(OnExposure);
    }

    private void OnExposure(Entity<NCMedicalBodyComponent> body, ref NCSystemicExposureEvent args)
    {
        if (args.Handled)
            return;

        var damage = new DamageSpecifier();
        var map = new Dictionary<ProtoId<DamageTypePrototype>, ProtoId<NCSystemicDamageProfilePrototype>>
        {
            [args.DamageType] = args.Profile,
        };
        damage.DamageDict[args.DamageType] = Content.Shared.FixedPoint.FixedPoint2.New(args.Amount);
        args.Handled = ApplyDamagePayload(
            body.Owner,
            args.ResolutionId,
            args.Source,
            args.Sequence,
            damage,
            map).Accepted;
    }

    public NCSystemicStateComponent EnsureInitialized(EntityUid body)
    {
        var state = EnsureComp<NCSystemicStateComponent>(body);
        if (state.StateInitialized &&
            !string.IsNullOrWhiteSpace(state.Profile.Id) &&
            _prototypes.TryIndex(state.Profile, out NCSystemicPhysiologyProfilePrototype? existing))
        {
            ValidateProfile(existing);
            NormalizeBurdens(state, existing);
            return state;
        }

        var profile = GetDefaultProfile();
        ValidateProfile(profile);
        state.Profile = profile.ID;
        state.StateInitialized = true;
        state.Burdens.Clear();
        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
            state.Burdens[channel] = 0f;
        state.ShockableArrestLatched = false;
        state.ProcessedPayloads.Clear();
        state.ProcessedPayloadOrder.Clear();
        return state;
    }

    /// <summary>
    /// Trusted server entry point for richer combat adapters. It consumes only types present in the supplied
    /// systemic map and applies them exactly once for (body, action, source, sequence, impact).
    /// </summary>
    public NCSystemicPayloadResult ApplyDamagePayload(
        EntityUid body,
        NCActionResolutionId resolutionId,
        NCSystemicDamageSourceKind source,
        uint sequence,
        DamageSpecifier damage,
        IReadOnlyDictionary<ProtoId<DamageTypePrototype>, ProtoId<NCSystemicDamageProfilePrototype>> systemicProfiles)
    {
        return ApplyDamagePayload(
            body,
            resolutionId,
            source,
            sequence,
            0,
            damage,
            systemicProfiles);
    }

    /// <summary>
    /// Rich impact overload. The source sequence identifies the projectile or attack within an action, while
    /// impactSequence identifies one accepted physical contact made by that source.
    /// </summary>
    public NCSystemicPayloadResult ApplyDamagePayload(
        EntityUid body,
        NCActionResolutionId resolutionId,
        NCSystemicDamageSourceKind source,
        uint sequence,
        uint impactSequence,
        DamageSpecifier damage,
        IReadOnlyDictionary<ProtoId<DamageTypePrototype>, ProtoId<NCSystemicDamageProfilePrototype>> systemicProfiles)
    {
        if (!resolutionId.IsValid ||
            !Enum.IsDefined(source) ||
            !TryGetProfile(body, out var state, out var physiologyProfile))
        {
            return default;
        }

        var selected = damage.DamageDict
            .Where(entry => entry.Value != Content.Shared.FixedPoint.FixedPoint2.Zero &&
                            systemicProfiles.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key.Id, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value, Profile: systemicProfiles[entry.Key]))
            .ToArray();
        foreach (var (_, _, profileId) in selected)
        {
            if (!_prototypes.TryIndex(profileId, out NCSystemicDamageProfilePrototype? profile) ||
                !ValidateDamageProfile(profile) ||
                !physiologyProfile.Channels.ContainsKey(profile.Channel))
            {
                return default;
            }
        }

        // A payload without a systemic channel does not consume its correlation key. This allows a source
        // to enrich the same still-unresolved impact later without turning the real exposure into a no-op.
        if (selected.Length == 0)
            return new NCSystemicPayloadResult(true, false, 0);

        var key = new NCProcessedSystemicPayloadKey(
            resolutionId.Value,
            source,
            sequence,
            impactSequence);
        if (!state.ProcessedPayloads.Add(key))
            return new NCSystemicPayloadResult(true, true, 0);

        var capacity = Math.Max(1, physiologyProfile.ProcessedPayloadCapacity);
        while (state.ProcessedPayloadOrder.Count >= capacity)
            state.ProcessedPayloads.Remove(state.ProcessedPayloadOrder.Dequeue());
        state.ProcessedPayloadOrder.Enqueue(key);

        var applied = 0;
        foreach (var (_, amount, profileId) in selected)
        {
            if (ApplyResolved(body, state, physiologyProfile, profileId, amount.Float()).Accepted)
                applied++;
        }

        return new NCSystemicPayloadResult(true, false, applied);
    }

    /// <summary>
    /// Applies a signed compatibility amount. Positive values add measured exposure; negative values invoke
    /// the data-defined recovery side of the same profile. A healthy zero result is still an accepted action.
    /// </summary>
    public NCSystemicApplicationResult Apply(
        EntityUid body,
        ProtoId<NCSystemicDamageProfilePrototype> damageProfileId,
        float amount)
    {
        if (!HasComp<NCMedicalBodyComponent>(body) ||
            !float.IsFinite(amount) ||
            amount == 0f ||
            !_prototypes.TryIndex(damageProfileId, out NCSystemicDamageProfilePrototype? damageProfile) ||
            !TryGetProfile(body, out var state, out var profile) ||
            !profile.Channels.TryGetValue(damageProfile.Channel, out var channelProfile) ||
            !ValidateDamageProfile(damageProfile))
        {
            return default;
        }

        return ApplyResolved(body, state, profile, damageProfileId, amount);
    }

    private NCSystemicApplicationResult ApplyResolved(
        EntityUid body,
        NCSystemicStateComponent state,
        NCSystemicPhysiologyProfilePrototype profile,
        ProtoId<NCSystemicDamageProfilePrototype> damageProfileId,
        float amount)
    {
        if (!_prototypes.TryIndex(damageProfileId, out NCSystemicDamageProfilePrototype? damageProfile) ||
            !profile.Channels.TryGetValue(damageProfile.Channel, out var channelProfile) ||
            !ValidateDamageProfile(damageProfile) ||
            !float.IsFinite(amount) || amount == 0f)
        {
            return default;
        }

        var previous = state.Burdens.GetValueOrDefault(damageProfile.Channel);
        var delta = amount > 0f
            ? amount * damageProfile.ExposurePerDamage
            : amount * damageProfile.RecoveryPerHealing;
        var current = Math.Clamp(previous + delta, 0f, channelProfile.MaximumBurden);
        state.Burdens[damageProfile.Channel] = current;
        UpdateArrestLatch(state, profile);

        if (!MathHelper.CloseTo(previous, current))
        {
            MarkChanged(body);
            var changed = new NCSystemicStateChangedEvent(
                damageProfile.Channel,
                previous,
                current,
                state.ShockableArrestLatched);
            RaiseLocalEvent(body, ref changed);
        }

        return new NCSystemicApplicationResult(
            true,
            damageProfile.Channel,
            previous,
            current,
            state.ShockableArrestLatched);
    }

    /// <summary>
    /// Advances natural clearance for one already-scheduled body. Permanent radiation or cellular burden does
    /// not create a useless recurring update when its configured recovery rate is zero.
    /// </summary>
    public bool AdvanceBody(EntityUid body, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || !TryGetProfile(body, out var state, out var profile))
            return false;

        var seconds = (float) elapsed.TotalSeconds;
        var changed = false;
        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
        {
            var channelProfile = profile.Channels[channel];
            var previous = state.Burdens.GetValueOrDefault(channel);
            if (previous <= 0f || channelProfile.NaturalRecoveryPerSecond <= 0f)
                continue;

            var current = MathF.Max(0f, previous - channelProfile.NaturalRecoveryPerSecond * seconds);
            if (MathHelper.CloseTo(previous, current))
                continue;

            state.Burdens[channel] = current;
            changed = true;
        }

        UpdateArrestLatch(state, profile);
        if (changed)
        {
            _medicalBodies.TryMarkDirty(
                body,
                NCMedicalDirtyDomains.Vascular |
                NCMedicalDirtyDomains.OrganFunction |
                NCMedicalDirtyDomains.Consciousness |
                NCMedicalDirtyDomains.Diagnostics |
                NCMedicalDirtyDomains.Persistence,
                false);
        }

        return changed;
    }

    public NCSystemicPhysiologyEffects GetPhysiologyEffects(EntityUid body)
    {
        if (!TryGetProfile(body, out var state, out var profile))
            return new NCSystemicPhysiologyEffects(1f, 1f, 0f, 1f, 0f, false, false);

        var oxygenPenalty = 0f;
        var cardiacPenalty = 0f;
        var shock = 0f;
        var brainPenalty = 0f;
        var rhythmInstability = 0f;
        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
        {
            var settings = profile.Channels[channel];
            var fraction = Math.Clamp(
                state.Burdens.GetValueOrDefault(channel) / settings.MaximumBurden,
                0f,
                1f);
            oxygenPenalty += fraction * settings.OxygenationPenaltyAtMaximum;
            cardiacPenalty += fraction * settings.CardiacOutputPenaltyAtMaximum;
            shock += fraction * settings.ShockContributionAtMaximum;
            brainPenalty += fraction * settings.BrainFunctionPenaltyAtMaximum;
            rhythmInstability += fraction * settings.RhythmInstabilityAtMaximum;
        }

        var boundedInstability = Math.Clamp(rhythmInstability, 0f, 1f);
        return new NCSystemicPhysiologyEffects(
            1f - Math.Clamp(oxygenPenalty, 0f, 1f),
            1f - Math.Clamp(cardiacPenalty, 0f, 1f),
            Math.Clamp(shock, 0f, 1f),
            1f - Math.Clamp(brainPenalty, 0f, 1f),
            boundedInstability,
            boundedInstability >= profile.UnstableRhythmThreshold,
            state.ShockableArrestLatched);
    }

    public float GetBurden(EntityUid body, NCSystemicChannel channel)
    {
        return TryGetProfile(body, out var state, out _)
            ? state.Burdens.GetValueOrDefault(channel)
            : 0f;
    }

    public bool ResetBody(EntityUid body)
    {
        if (!TryGetProfile(body, out var state, out _))
            return false;

        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
            state.Burdens[channel] = 0f;
        state.ShockableArrestLatched = false;
        state.ProcessedPayloads.Clear();
        state.ProcessedPayloadOrder.Clear();
        return true;
    }

    public TimeSpan? GetNextUpdateDelay(EntityUid body)
    {
        if (!TryGetProfile(body, out var state, out var profile))
            return null;

        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
        {
            if (state.Burdens.GetValueOrDefault(channel) > 0f &&
                profile.Channels[channel].NaturalRecoveryPerSecond > 0f)
            {
                return profile.ActiveUpdateInterval;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts the latched rhythm after a real defibrillator action. It removes only enough electrical
    /// instability to avoid immediate re-arrest; toxin/radiation/cellular causes remain authoritative.
    /// </summary>
    public bool ResolveDefibrillation(EntityUid body)
    {
        if (!TryGetProfile(body, out var state, out var profile) || !state.ShockableArrestLatched)
            return false;

        state.ShockableArrestLatched = false;
        var electrical = profile.Channels[NCSystemicChannel.Electrical];
        if (electrical.RhythmInstabilityAtMaximum > 0f)
        {
            var otherInstability = CalculateRhythmInstability(state, profile, NCSystemicChannel.Electrical);
            var allowedElectrical = MathF.Max(0f, profile.DefibrillationReleaseThreshold - otherInstability);
            var allowedFraction = Math.Clamp(
                allowedElectrical / electrical.RhythmInstabilityAtMaximum,
                0f,
                1f);
            state.Burdens[NCSystemicChannel.Electrical] = MathF.Min(
                state.Burdens.GetValueOrDefault(NCSystemicChannel.Electrical),
                electrical.MaximumBurden * allowedFraction);
        }

        MarkChanged(body);
        return true;
    }

    private bool TryGetProfile(
        EntityUid body,
        out NCSystemicStateComponent state,
        out NCSystemicPhysiologyProfilePrototype profile)
    {
        state = default!;
        profile = default!;
        if (!HasComp<NCMedicalBodyComponent>(body))
            return false;

        state = EnsureInitialized(body);
        return _prototypes.TryIndex(state.Profile, out profile!);
    }

    private NCSystemicPhysiologyProfilePrototype GetDefaultProfile()
    {
        var profiles = _prototypes.EnumeratePrototypes<NCSystemicPhysiologyProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToList();
        if (profiles.Count != 1)
            throw new InvalidOperationException("Exactly one default human ncSystemicPhysiologyProfile must be defined.");
        return profiles[0];
    }

    private static void NormalizeBurdens(
        NCSystemicStateComponent state,
        NCSystemicPhysiologyProfilePrototype profile)
    {
        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
        {
            state.Burdens[channel] = Math.Clamp(
                state.Burdens.GetValueOrDefault(channel),
                0f,
                profile.Channels[channel].MaximumBurden);
        }

        foreach (var unknown in state.Burdens.Keys.Where(channel => !Enum.IsDefined(channel)).ToArray())
            state.Burdens.Remove(unknown);
    }

    private static void UpdateArrestLatch(
        NCSystemicStateComponent state,
        NCSystemicPhysiologyProfilePrototype profile)
    {
        if (!state.ShockableArrestLatched &&
            CalculateRhythmInstability(state, profile) >= profile.ShockableArrestThreshold)
        {
            state.ShockableArrestLatched = true;
        }
    }

    private static float CalculateRhythmInstability(
        NCSystemicStateComponent state,
        NCSystemicPhysiologyProfilePrototype profile,
        NCSystemicChannel? excluded = null)
    {
        var total = 0f;
        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
        {
            if (channel == excluded)
                continue;

            var settings = profile.Channels[channel];
            var fraction = Math.Clamp(
                state.Burdens.GetValueOrDefault(channel) / settings.MaximumBurden,
                0f,
                1f);
            total += fraction * settings.RhythmInstabilityAtMaximum;
        }
        return Math.Clamp(total, 0f, 1f);
    }

    private void MarkChanged(EntityUid body)
    {
        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
    }

    private static bool ValidateDamageProfile(NCSystemicDamageProfilePrototype profile)
    {
        return Enum.IsDefined(profile.Channel) &&
               float.IsFinite(profile.ExposurePerDamage) &&
               profile.ExposurePerDamage > 0f &&
               float.IsFinite(profile.RecoveryPerHealing) &&
               profile.RecoveryPerHealing > 0f;
    }

    private static void ValidateProfile(NCSystemicPhysiologyProfilePrototype profile)
    {
        var channels = Enum.GetValues<NCSystemicChannel>();
        if (profile.Channels.Count != channels.Length ||
            channels.Any(channel => !profile.Channels.ContainsKey(channel)) ||
            !FiniteUnit(profile.UnstableRhythmThreshold) ||
            !FiniteUnit(profile.ShockableArrestThreshold) ||
            !FiniteUnit(profile.DefibrillationReleaseThreshold) ||
            profile.UnstableRhythmThreshold >= profile.ShockableArrestThreshold ||
            profile.DefibrillationReleaseThreshold >= profile.ShockableArrestThreshold ||
            profile.ActiveUpdateInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"NC systemic physiology profile {profile.ID} is invalid.");
        }

        if (profile.ProcessedPayloadCapacity <= 0)
            throw new InvalidOperationException($"NC systemic profile {profile.ID} has invalid replay capacity.");

        foreach (var settings in profile.Channels.Values)
        {
            if (!float.IsFinite(settings.MaximumBurden) || settings.MaximumBurden <= 0f ||
                !float.IsFinite(settings.NaturalRecoveryPerSecond) || settings.NaturalRecoveryPerSecond < 0f ||
                !FiniteUnit(settings.OxygenationPenaltyAtMaximum) ||
                !FiniteUnit(settings.CardiacOutputPenaltyAtMaximum) ||
                !FiniteUnit(settings.ShockContributionAtMaximum) ||
                !FiniteUnit(settings.BrainFunctionPenaltyAtMaximum) ||
                !FiniteUnit(settings.RhythmInstabilityAtMaximum))
            {
                throw new InvalidOperationException($"NC systemic channel in profile {profile.ID} is invalid.");
            }
        }
    }

    private static bool FiniteUnit(float value)
    {
        return float.IsFinite(value) && value >= 0f && value <= 1f;
    }
}
