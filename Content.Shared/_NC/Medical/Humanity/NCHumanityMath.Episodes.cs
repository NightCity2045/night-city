// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Shared._NC.Medical.Humanity;

public static partial class NCHumanityMath
{
    public static float EpisodeRisk(NCHumanityStateSnapshot state) => state.Burden <= 0f ? 0f :
        Math.Clamp(state.Stress + (Base - state.Current) * .25f + state.Exhaustion * .15f, 0f, Base);

    private static void AdvanceEpisode(NCHumanityStateSnapshot state, NCHumanityProfilePrototype profile, float seconds)
    {
        state.MotorInhibitSeconds = Math.Max(0f, state.MotorInhibitSeconds - seconds);
        state.SupportSeconds = Math.Max(0f, state.SupportSeconds - seconds);
        state.SupportCooldown = Math.Max(0f, state.SupportCooldown - seconds);
        state.SymptomSeconds = Math.Max(0f, state.SymptomSeconds - seconds);
        state.Exhaustion = Math.Clamp(state.Exhaustion + seconds * (state.Stage switch
        {
            NCCyberCrisisStage.Acute => .12f,
            NCCyberCrisisStage.Crisis => .03f,
            _ => state.Stress < 35f ? -.04f : 0f,
        }), 0f, Base);
        if (state.Stage == NCCyberCrisisStage.Recovery)
        {
            state.RecoverySeconds = Math.Max(0f, state.RecoverySeconds - seconds);
            // Guaranteed refractory interval; no immediate relapse and no free humanity restoration.
            if (state.RecoverySeconds <= 0f)
            {
                state.Stage = NCCyberCrisisStage.Stable;
                state.Episode = null;
            }
            return;
        }
        if (state.Stage == NCCyberCrisisStage.Acute)
        {
            state.AcuteSeconds += seconds;
            if (state.AcuteSeconds >= profile.MaximumAcuteSeconds)
            {
                BeginRecovery(state, profile);
                return;
            }
        }
        var risk = EpisodeRisk(state);
        var desired = NCCyberCrisisStage.Stable;
        var threshold = 0f;
        foreach (var band in profile.Bands)
        {
            if (band.Stage == state.Stage) threshold = band.StressAtLeast;
            if (state.Burden > 0f && state.Stress >= 20f && risk >= band.StressAtLeast && band.Stage > desired)
                desired = band.Stage;
        }
        if (desired > state.Stage)
        {
            state.StabilizingSeconds = 0f;
            state.EscalatingSeconds += seconds;
            if (state.EscalatingSeconds >= profile.EscalationSeconds)
            {
                state.Stage++;
                state.EscalatingSeconds = 0f;
                state.SymptomSeconds = 0f;
            }
            return;
        }
        state.EscalatingSeconds = 0f;
        var recovering = desired < state.Stage && (state.Burden <= 0f || state.Stress < 15f || risk < threshold - profile.StressHysteresis);
        state.StabilizingSeconds = recovering ? state.StabilizingSeconds + seconds : 0f;
        if (state.StabilizingSeconds >= profile.StabilizationSeconds)
        {
            if (state.Stage is NCCyberCrisisStage.Acute or NCCyberCrisisStage.Crisis)
                BeginRecovery(state, profile);
            else
            {
                state.Stage = NCCyberCrisisStage.Stable;
                state.StabilizingSeconds = 0f;
                state.Episode = null;
            }
        }
    }

    private static void BeginRecovery(NCHumanityStateSnapshot state, NCHumanityProfilePrototype profile)
    {
        state.Stage = NCCyberCrisisStage.Recovery;
        state.RecoverySeconds = profile.RecoverySeconds;
        state.AcuteSeconds = 0f;
        state.StabilizingSeconds = 0f;
        state.EscalatingSeconds = 0f;
        state.SymptomSeconds = 0f;
    }
}
