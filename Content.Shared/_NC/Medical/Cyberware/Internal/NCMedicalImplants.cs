// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.Chemistry.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware.Internal;

[Prototype("ncCyberwareQuality")]
public sealed partial class NCCyberwareQualityPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name;
    [DataField] public float Performance = 1;
    [DataField] public float MeasurementStep = 1;
}

[RegisterComponent]
public sealed partial class NCMedicalControllerComponent : Component
{
    [DataField] public ProtoId<NCCyberwareFirmwarePrototype>? Firmware;
}

[RegisterComponent]
public sealed partial class NCMedicalImplantComponent : Component
{
    [DataField(required: true)] public ProtoId<NCCyberwareFirmwarePrototype> RequiredFirmware;
    [DataField] public bool Injector;
    [DataField] public NCMedicalImplantState State = new();
    public TimeSpan NextSample;
    public TimeSpan NextAlarm;
    public TimeSpan SampledAt;
    public bool HasSample;
    public float Oxygen;
    public float Perfusion;
    public float Pain;
    public float Blood;
}

/// <summary>Private persistent settings and cartridge contents. Runtime readings are never restored as live data.</summary>
[DataDefinition]
public sealed partial class NCMedicalImplantState
{
    [DataField] public Solution Cartridge = new() { MaxVolume = 30 };
    [DataField] public float Dose = 2;
    [DataField] public float IntervalSeconds = 60;
    [DataField] public float Limit = 10;
    [DataField] public float Delivered;
    [DataField] public float CooldownSeconds;
    [DataField] public bool Automatic;
    [DataField] public NCMedicalTrigger Trigger;
    [DataField] public float Threshold = 70;
}

[Serializable, NetSerializable]
public enum NCMedicalTrigger : byte { OxygenBelow, PerfusionBelow, PainAbove }

public static class NCMedicalImplantMath
{
    public static NCMedicalImplantState Clone(NCMedicalImplantState state) => new()
    {
        Cartridge = state.Cartridge.Clone(), Dose = state.Dose, IntervalSeconds = state.IntervalSeconds,
        Limit = state.Limit, Delivered = state.Delivered, CooldownSeconds = state.CooldownSeconds,
        Automatic = state.Automatic, Trigger = state.Trigger, Threshold = state.Threshold,
    };

    public static bool Valid(NCMedicalImplantState state) =>
        ValidSettings(state.Dose, state.IntervalSeconds, state.Limit, state.Threshold, state.Trigger) &&
        float.IsFinite(state.Delivered) && state.Delivered is >= 0 and <= 30 &&
        float.IsFinite(state.CooldownSeconds) && state.CooldownSeconds is >= 0 and <= 600 &&
        state.Cartridge != null && state.Cartridge.Volume <= 30;

    public static bool ValidSettings(float dose, float interval, float limit, float threshold, NCMedicalTrigger trigger) =>
        float.IsFinite(dose) && dose is >= .5f and <= 5 &&
        float.IsFinite(interval) && interval is >= 30 and <= 600 &&
        float.IsFinite(limit) && limit >= dose && limit <= 30 &&
        float.IsFinite(threshold) && threshold is >= 0 and <= 100 && Enum.IsDefined(trigger);

    public static bool CanDose(float dose, float delivered, float limit, float cooldown, float available) =>
        float.IsFinite(dose) && dose > 0 && float.IsFinite(delivered) && delivered >= 0 &&
        float.IsFinite(limit) && delivered + dose <= limit && float.IsFinite(cooldown) && cooldown == 0 &&
        float.IsFinite(available) && available >= dose;

    public static float MeteredDose(float requested, float step) => Math.Clamp(Quantize(requested, step / 4), .1f, 5f);

    public static float Quantize(float value, float step) =>
        float.IsFinite(value) && float.IsFinite(step) && step > 0 ? MathF.Round(value / step) * step : 0;
}
