// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Prototypes;

/// <summary>Data-authored layers and transition thresholds for one vehicle's acoustic identity.</summary>
[Prototype("ncVehicleSoundProfile")]
public sealed partial class NCVehicleSoundProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public SoundSpecifier? Startup;
    [DataField] public SoundSpecifier? Shutdown;
    [DataField(required: true)] public SoundSpecifier LowLoop = default!;
    [DataField(required: true)] public SoundSpecifier MidLoop = default!;
    [DataField(required: true)] public SoundSpecifier HighLoop = default!;
    [DataField] public SoundSpecifier? ForcedInductionLoop;
    [DataField] public SoundSpecifier? ThrottleRelease;
    [DataField] public SoundSpecifier? Backfire;
    [DataField] public SoundSpecifier? Shift;
    [DataField] public SoundSpecifier? TyreSlipLoop;
    [DataField] public SoundSpecifier? Horn;
    [DataField] public float MinimumLoopVolume = -48;
    [DataField] public float MaximumLoopVolume = -4;
    [DataField] public float LoadVolumeBoost = 3;
    [DataField] public float ForcedInductionStart = 0.35f;
    [DataField] public float SlipStart = 0.3f;
    [DataField] public float ReleaseFromThrottle = 0.65f;
    [DataField] public float ReleaseToThrottle = 0.15f;
    [DataField] public float ReleaseMinimumRpm = 0.45f;
    [DataField] public float ReleaseCooldown = 0.8f;
}
