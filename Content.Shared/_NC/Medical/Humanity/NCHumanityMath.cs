// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Humanity;

/// <summary>Deterministic package accounting and bounded psychological state; never changes physical health or control.</summary>
public static class NCHumanityMath
{
    public const float Base = 100f;
    public const int MaximumAdaptations = 4096;
    public const int MaximumRecentImpulses = 32;
    public static float Maximum(float burden) => MathF.Max(0f, Base - burden);

    public static bool ValidateProfile(NCHumanityProfilePrototype profile)
    {
        if (profile.ActiveUpdateInterval <= TimeSpan.Zero || profile.ActiveUpdateInterval > TimeSpan.FromMinutes(1) ||
            !FiniteRange(profile.SafeRecoveryPerSecond, 0f, 100f) || !FiniteRange(profile.OverloadPerSecond, 0f, 100f) ||
            !FiniteRange(profile.OverburdenPerSecond, 0f, 100f) || !FiniteRange(profile.VulnerabilityScale, 0f, 100f) ||
            !FiniteRange(profile.StableMinimumPerfusion, 0f, 1f) || !FiniteRange(profile.StableMinimumOxygenation, 0f, 1f) ||
            !FiniteRange(profile.StableMaximumShock, 0f, 1f) || !FiniteRange(profile.NeuralTraumaThreshold, 0f, 1f) ||
            !FiniteRange(profile.StressHysteresis, 0f, 100f) || !FiniteRange(profile.HumanityHysteresis, 0f, 100f) ||
            !FiniteRange(profile.StabilizationSeconds, 0.01f, 86400f) || profile.Bands == null || profile.Bands.Count != 3)
            return false;
        var seen = new HashSet<NCCyberCrisisStage>();
        foreach (var band in profile.Bands)
        {
            if (band == null || !Enum.IsDefined(band.Stage) || band.Stage == NCCyberCrisisStage.Stable || !seen.Add(band.Stage) ||
                !FiniteRange(band.HumanityBelow, 0f, 100f) || !FiniteRange(band.StressAtLeast, 0f, 100f) ||
                !FiniteRange(band.EnhancementEfficiency, 0.1f, 1f)) return false;
            foreach (var other in profile.Bands)
                if (other != null && other.Stage > band.Stage && (other.HumanityBelow >= band.HumanityBelow ||
                    other.StressAtLeast <= band.StressAtLeast || other.EnhancementEfficiency > band.EnhancementEfficiency)) return false;
        }
        return true;
    }

    public static bool Validate(NCHumanityStateSnapshot state)
    {
        if (!FiniteRange(state.Burden, 0f, 100000f) || !FiniteRange(state.Current, 0f, Maximum(state.Burden)) ||
            !FiniteRange(state.Stress, 0f, 100f) || !Enum.IsDefined(state.Stage) ||
            !FiniteRange(state.StabilizingSeconds, 0f, 86400f) || state.Adaptation == null ||
            state.Adaptation.Count > MaximumAdaptations || state.RecentImpulses == null ||
            state.RecentImpulses.Count > MaximumRecentImpulses)
            return false;
        foreach (var (key, acute) in state.Adaptation)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 192 || !FiniteRange(acute, 0f, Base))
                return false;
        }
        var sources = new HashSet<Guid>();
        foreach (var memory in state.RecentImpulses)
        {
            if (memory == null || memory.Source == Guid.Empty || !sources.Add(memory.Source) ||
                string.IsNullOrWhiteSpace(memory.Profile.Id) || !FiniteRange(memory.RemainingSeconds, 0f, 86400f))
                return false;
        }
        return true;
    }

    public static bool TryReconcile(NCHumanityStateSnapshot state,
        IReadOnlyDictionary<string, NCHumanityPackageDose> packages, bool creditExisting = false)
    {
        if (!Validate(state) || packages.Count > 256)
            return false;
        var burden = 0f;
        var acute = 0f;
        var additions = 0;
        foreach (var (key, dose) in packages)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 192 || !FiniteRange(dose.Burden, 0f, Base) ||
                !FiniteRange(dose.AcuteCost, 0f, Base))
                return false;
            burden += dose.Burden;
            acute += creditExisting ? 0f : MathF.Max(0f, dose.AcuteCost - state.Adaptation.GetValueOrDefault(key));
            if (!state.Adaptation.ContainsKey(key)) additions++;
        }
        if (state.Adaptation.Count + additions > MaximumAdaptations)
            return false;
        foreach (var (key, dose) in packages)
            state.Adaptation[key] = MathF.Max(state.Adaptation.GetValueOrDefault(key), dose.AcuteCost);
        state.Burden = burden;
        state.Current = Math.Clamp(state.Current - acute, 0f, Maximum(burden));
        return true;
    }

    public static void Advance(NCHumanityStateSnapshot state, NCHumanityProfilePrototype profile,
        float seconds, float recoveryPerSecond, float suppressionPerSecond, bool overloaded, bool stable)
    {
        if (!Validate(state) || !ValidateProfile(profile) || !FiniteRange(seconds, 0f, 86400f) ||
            !float.IsFinite(recoveryPerSecond) || recoveryPerSecond < 0f ||
            !float.IsFinite(suppressionPerSecond) || suppressionPerSecond < 0f)
            return;
        state.Current = MathF.Min(Maximum(state.Burden), state.Current + recoveryPerSecond * seconds);
        var vulnerability = 1f + profile.VulnerabilityScale * (1f - state.Current / Base);
        var increase = (overloaded ? profile.OverloadPerSecond * vulnerability : 0f) +
                       profile.OverburdenPerSecond * MathF.Max(0f, state.Burden - Base);
        var decrease = suppressionPerSecond + (stable && !overloaded ? profile.SafeRecoveryPerSecond : 0f);
        state.Stress = Math.Clamp(state.Stress + (increase - decrease) * seconds, 0f, 100f);
        foreach (var memory in state.RecentImpulses)
            memory.RemainingSeconds = MathF.Max(0f, memory.RemainingSeconds - seconds);
        UpdateStage(state, profile, seconds);
    }

    public static void UpdateStage(NCHumanityStateSnapshot state, NCHumanityProfilePrototype profile, float seconds)
    {
        if (!Validate(state) || !ValidateProfile(profile) || !FiniteRange(seconds, 0f, 86400f)) return;
        var desired = NCCyberCrisisStage.Stable;
        NCHumanityCrisisBand? currentBand = null;
        foreach (var band in profile.Bands)
        {
            if (band.Stage == state.Stage) currentBand = band;
            if (state.Burden > 0f && state.Current < band.HumanityBelow && state.Stress >= band.StressAtLeast &&
                band.Stage > desired)
                desired = band.Stage;
        }
        if (desired >= state.Stage)
        {
            state.Stage = desired;
            state.StabilizingSeconds = 0f;
            return;
        }
        var canRecover = state.Burden <= 0f || currentBand == null ||
                         state.Stress < currentBand.StressAtLeast - profile.StressHysteresis ||
                         state.Current >= currentBand.HumanityBelow + profile.HumanityHysteresis;
        state.StabilizingSeconds = canRecover ? state.StabilizingSeconds + seconds : 0f;
        if (state.StabilizingSeconds >= profile.StabilizationSeconds)
        {
            state.Stage = desired;
            state.StabilizingSeconds = 0f;
        }
    }

    public static float TreatmentFactor(float concentration, float minimum, float therapeutic, float toxic, float maximum)
    {
        if (!float.IsFinite(concentration) || concentration < minimum || therapeutic <= 0f || maximum <= toxic)
            return 0f;
        var toxicPenalty = Math.Clamp((concentration - toxic) / (maximum - toxic), 0f, 1f);
        return Math.Clamp(concentration / therapeutic, 0f, 1f) * (1f - toxicPenalty);
    }

    public static NCHumanityStateSnapshot Clone(NCHumanityStateSnapshot source) => new()
    {
        Current = source.Current, Burden = source.Burden, Stress = source.Stress, Stage = source.Stage,
        StabilizingSeconds = source.StabilizingSeconds, Adaptation = new(source.Adaptation),
        RecentImpulses = source.RecentImpulses.ConvertAll(memory => new NCHumanityImpulseMemory
        { Source = memory.Source, Profile = memory.Profile, RemainingSeconds = memory.RemainingSeconds }),
    };

    private static bool FiniteRange(float value, float minimum, float maximum)
        => float.IsFinite(value) && value >= minimum && value <= maximum;
}
