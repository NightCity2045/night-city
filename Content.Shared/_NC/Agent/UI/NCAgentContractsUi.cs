// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.UI;

[Serializable, NetSerializable]
public enum NCPlayerContractStatus : byte { Open, Accepted, Ready, Completed, Cancelled, Withdrawn, Expired }

[Serializable, NetSerializable]
public enum NCPlayerContractAction : byte { Accept, Ready, Complete, Cancel, Withdraw }

/// <summary>Reward is a promise recorded by the customer, never a payment instruction.</summary>
[Serializable, NetSerializable]
public sealed record NCPlayerContractEntry(string Id, long Version, int Customer, string CustomerName,
    int? Contractor, string ContractorName, string Title, string Terms, string Reward,
    NCPlayerContractStatus Status, long? RemainingSeconds);

[Serializable, NetSerializable]
public sealed class NCAgentContractsUpdateMessage(List<NCPlayerContractEntry> entries, int viewer,
    bool history, string? nextCursor, string notice) : BoundUserInterfaceMessage
{
    public readonly List<NCPlayerContractEntry> Entries = entries;
    public readonly int Viewer = viewer;
    public readonly bool History = history;
    public readonly string? NextCursor = nextCursor;
    public readonly string Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCAgentContractsPageMessage(bool history, string? cursor) : BoundUserInterfaceMessage
{
    public readonly bool History = history;
    public readonly string? Cursor = cursor;
}

[Serializable, NetSerializable]
public sealed class NCAgentContractCreateMessage(Guid commandId, string title, string terms, string reward,
    int deadlineMinutes) : BoundUserInterfaceMessage
{
    public readonly Guid CommandId = commandId;
    public readonly string Title = title;
    public readonly string Terms = terms;
    public readonly string Reward = reward;
    public readonly int DeadlineMinutes = deadlineMinutes;
}

[Serializable, NetSerializable]
public sealed class NCAgentContractActionMessage(string id, long version, NCPlayerContractAction action)
    : BoundUserInterfaceMessage
{
    public readonly string Id = id;
    public readonly long Version = version;
    public readonly NCPlayerContractAction Action = action;
}
