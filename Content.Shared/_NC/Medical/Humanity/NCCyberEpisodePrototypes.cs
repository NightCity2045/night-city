// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Humanity;

[Prototype("ncCyberEpisode")]
public sealed partial class NCCyberEpisodePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name { get; private set; }
    [DataField] public string PackageKey { get; private set; } = "";
    [DataField(required: true)] public List<ProtoId<NCCyberSymptomPrototype>> Symptoms { get; private set; } = [];
}

[Prototype("ncCyberSymptom")]
public sealed partial class NCCyberSymptomPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Message { get; private set; }
    [DataField] public NCCyberCrisisStage MinimumStage { get; private set; } = NCCyberCrisisStage.Warning;
    [DataField] public float Duration { get; private set; } = 3f;
    [DataField] public float Cooldown { get; private set; } = 12f;
    [DataField] public bool Phantom { get; private set; }
    [DataField] public bool Threat { get; private set; }
    [DataField] public bool BodyEcho { get; private set; }
    [DataField] public bool ShootAtViewer { get; private set; }
    [DataField] public string InhandRsi { get; private set; } = "Objects/Weapons/Guns/Pistols/mk58.rsi";
    [DataField] public SoundSpecifier? ShotSound { get; private set; }
    [DataField] public bool MotorWarning { get; private set; }
    [DataField] public SoundSpecifier? Sound { get; private set; }
}

// Private, short-lived presentation; never broadcasts a patient's psychological state through PVS.
[Serializable, NetSerializable]
public sealed class NCCyberEpisodeViewEvent : EntityEventArgs
{
    public NetEntity Body;
    public NetEntity? PerceivedTarget;
    public NCHumanityStatus Status;
    public ProtoId<NCCyberEpisodePrototype>? Episode;
    public ProtoId<NCCyberSymptomPrototype>? Symptom;
    public uint Sequence;
    public bool DeclinesSupport;
}

[Serializable, NetSerializable]
public sealed class NCCyberEpisodeActionEvent : EntityEventArgs
{
    public bool DisableEnhancements;
    public bool ToggleSupport;
}

[Serializable, NetSerializable]
public sealed class NCCyberEpisodeOpenEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed partial class NCGroundingDoAfterEvent : Content.Shared.DoAfter.SimpleDoAfterEvent
{
    [DataField] public NetEntity Brain;
}
