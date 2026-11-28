// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server._NC.Bank;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Dispatch;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Dispatch;

/// <summary>One NCPD or Trauma Team incident. Incidents live for the current round only.</summary>
public sealed class NCDispatchIncident
{
    public int Id;
    public ProtoId<NCEmergencyServicePrototype> Service;
    public NCDispatchIncidentKind Kind;
    public NCDispatchStatus Status;

    /// <summary>The person the incident is about; the location follows them while they exist.</summary>
    public EntityUid? Subject;

    /// <summary>Who is told when the call is taken, the crew arrives or the call closes.</summary>
    public EntityUid? Caller;

    public string CallerName = string.Empty;
    public string Message = string.Empty;
    public NCMapCoordinates Location;
    public string LocationText = string.Empty;

    /// <summary>The call came from a district NCPD does not patrol.</summary>
    public bool Uncovered;

    public TimeSpan Created;
    public TimeSpan Changed;
    public TimeSpan LastShot;
    public int Shots;

    public EntityUid? Responder;
    public string ResponderName = string.Empty;

    /// <summary>The Trauma plan of the patient, if subscribed.</summary>
    public ProtoId<NCTraumaPlanPrototype>? Plan;

    /// <summary>Price paid for a one-off call.</summary>
    public long Paid;

    /// <summary>The account refunds go to.</summary>
    public NCBankAccountRecord? Client;

    /// <summary>The crew should be on scene by then; later arrival refunds the client.</summary>
    public TimeSpan? Deadline;

    public bool Refunded;
    public bool Arrived;
}
