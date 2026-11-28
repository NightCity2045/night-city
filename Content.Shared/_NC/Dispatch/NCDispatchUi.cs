// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.CartridgeLoader;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Dispatch;

[Serializable, NetSerializable]
public enum NCDispatchIncidentKind : byte
{
    /// <summary>A resident called from their PDA.</summary>
    Call,

    /// <summary>A Trauma subscriber's biomonitor reported a critical state or death.</summary>
    Biomonitor,

    /// <summary>Gunfire in a district under police coverage.</summary>
    Shooting,
}

[Serializable, NetSerializable]
public enum NCDispatchStatus : byte
{
    Open,
    Accepted,
    OnScene,
    Closed,
}

/// <summary>One incident as the dispatcher program shows it. Texts are prepared by the server.</summary>
[Serializable, NetSerializable]
public sealed record NCDispatchIncidentView(
    int Id,
    NCDispatchIncidentKind Kind,
    NCDispatchStatus Status,
    string Caller,
    string Message,
    string Location,
    int Z,
    TimeSpan Age,
    string Responder,
    bool Mine,
    string Plan,
    bool Uncovered,
    int Shots);

[Serializable, NetSerializable]
public sealed class NCDispatchCartridgeUiState(bool available, string service, List<NCDispatchIncidentView> incidents)
    : BoundUserInterfaceState
{
    public readonly bool Available = available;
    public readonly string Service = service;
    public readonly List<NCDispatchIncidentView> Incidents = incidents;
}

[Serializable, NetSerializable]
public sealed class NCDispatchCartridgeAcceptMessage(int id) : CartridgeMessageEvent
{
    public readonly int Id = id;
}

[Serializable, NetSerializable]
public sealed class NCDispatchCartridgeCloseMessage(int id) : CartridgeMessageEvent
{
    public readonly int Id = id;
}

/// <summary>A service the emergency program can call, with the caller's own call to it if one is active.</summary>
[Serializable, NetSerializable]
public sealed record NCEmergencyServiceView(
    string Id,
    string Name,
    long Price,
    TimeSpan Cooldown,
    int? CallId,
    NCDispatchStatus CallStatus,
    string Responder);

[Serializable, NetSerializable]
public sealed record NCTraumaPlanView(string Id, string Name, string Description, long Price, int ResponseMinutes);

[Serializable, NetSerializable]
public sealed class NCEmergencyCartridgeUiState(
    bool hasAccount,
    long balance,
    List<NCEmergencyServiceView> services,
    List<NCTraumaPlanView> plans,
    string? plan) : BoundUserInterfaceState
{
    public readonly bool HasAccount = hasAccount;
    public readonly long Balance = balance;
    public readonly List<NCEmergencyServiceView> Services = services;
    public readonly List<NCTraumaPlanView> Plans = plans;

    /// <summary>The caller's Trauma plan id, if subscribed.</summary>
    public readonly string? Plan = plan;
}

[Serializable, NetSerializable]
public sealed class NCEmergencyCallMessage(string service, string text) : CartridgeMessageEvent
{
    public readonly string Service = service;
    public readonly string Text = text;
}

[Serializable, NetSerializable]
public sealed class NCEmergencyCancelMessage(string service) : CartridgeMessageEvent
{
    public readonly string Service = service;
}

[Serializable, NetSerializable]
public sealed class NCTraumaSubscribeMessage(string plan) : CartridgeMessageEvent
{
    public readonly string Plan = plan;
}

[Serializable, NetSerializable]
public sealed class NCTraumaUnsubscribeMessage : CartridgeMessageEvent;

/// <summary>The PDA program residents call NCPD and Trauma Team from.</summary>
[RegisterComponent]
public sealed partial class NCEmergencyCartridgeComponent : Component;

/// <summary>The PDA program service members receive and take incidents in.</summary>
[RegisterComponent]
public sealed partial class NCDispatchCartridgeComponent : Component;
