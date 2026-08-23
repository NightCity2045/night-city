// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Police.NCPD;

[Serializable, NetSerializable]
public readonly record struct NCNcpdUnitId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCNcpdCommandId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCNcpdCommandKind : byte
{
    PatrolZone,
    CoverZone,
    HoldZone,
    RespondToIncident,
    SearchZone,
    InvestigateZone,
    ReturnToStation,
    StandDown,
}

[Serializable, NetSerializable]
public enum NCNcpdCommandStatus : byte
{
    Accepted,
    EnRoute,
    Executing,
    Completed,
    Failed,
    Cancelled,
}

[Serializable, NetSerializable]
public enum NCNcpdOfficerRole : byte
{
    Command,
    Dispatch,
    Investigator,
    Supervisor,
    PatrolOfficer,
    Cadet,
}

[Serializable, NetSerializable]
public enum NCNcpdAssignmentKind : byte
{
    StationCommand,
    Dispatch,
    Investigation,
    Patrol,
    Response,
    Detention,
    Evidence,
    Reserve,
}

[Serializable, NetSerializable]
public enum NCNcpdDutyStatus : byte
{
    OffDuty,
    ReportToStation,
    CheckIn,
    Available,
    Assigned,
    CheckOut,
}

[Serializable, NetSerializable]
public enum NCNcpdUnitRole : byte
{
    Leader,
    Partner,
    Cadet,
    Investigator,
    Reserve,
}

[Serializable, NetSerializable]
public enum NCNcpdForceLevel : byte
{
    Presence,
    VerbalOrder,
    Restraint,
    NonLethal,
    Lethal,
}
