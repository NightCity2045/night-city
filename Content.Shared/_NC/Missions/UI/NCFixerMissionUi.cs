// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Missions.UI;

[Serializable, NetSerializable]
public enum NCFixerMissionUiKey : byte { Key }
[Serializable, NetSerializable]
public enum NCFixerWorkState : byte { Planning, Deploying, Active, Completed, Cancelled, Failed }
[Serializable, NetSerializable]
public enum NCFixerWorkAction : byte { Invite, Join, Decline, Launch, Cancel, Deliver }

[Serializable, NetSerializable]
public sealed record NCFixerMissionOffer(string Definition, int Reward, bool Available)
{
    public string Location { get; init; } = string.Empty;
}
[Serializable, NetSerializable]
public sealed record NCFixerWorkEntry(string Id, long Version, string Definition, string ContactName,
    int Organizer, List<int> Crew, List<int> Invited, NCFixerWorkState State, int Reward, bool Paid,
    int? SecondsRemaining, int DeliverySecondsRemaining, bool Surrendered, string OrganizerName, List<string> CrewNames)
{
    public string Location { get; init; } = string.Empty;
}

[Serializable, NetSerializable]
public sealed record NCFixerReputationEntry(string ContactName, int Executor, int Organizer, int Trust,
    int OrganizerThreshold, int AvailableTiers, int TotalTiers);

[Serializable, NetSerializable]
public sealed class NCFixerMissionUpdateMessage(List<NCFixerMissionOffer> offers, List<NCFixerWorkEntry> work,
    int viewer, bool atContact, string notice, string contactStatus = "") : BoundUserInterfaceMessage
{
    public List<NCFixerReputationEntry> Reputation { get; init; } = [];
    public readonly List<NCFixerMissionOffer> Offers = offers;
    public readonly List<NCFixerWorkEntry> Work = work;
    public readonly int Viewer = viewer;
    public readonly bool AtContact = atContact;
    public readonly string Notice = notice;
    public readonly string ContactStatus = contactStatus;
}

[Serializable, NetSerializable]
public sealed class NCFixerMissionAcceptMessage(string definition) : BoundUserInterfaceMessage
{
    public readonly string Definition = definition;
}
[Serializable, NetSerializable]
public sealed class NCFixerMissionRefreshMessage : BoundUserInterfaceMessage;
[Serializable, NetSerializable]
public sealed class NCFixerWorkActionMessage(string id, long version, NCFixerWorkAction action, int target = 0)
    : BoundUserInterfaceMessage
{
    public readonly string Id = id;
    public readonly long Version = version;
    public readonly NCFixerWorkAction Action = action;
    public readonly int Target = target;
}
