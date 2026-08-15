// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.CartridgeLoader;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.MedTek;

[Serializable, NetSerializable]
public sealed class NCAgentMedTekUiState(List<NCAgentMedicalScan> history) : BoundUserInterfaceState
{
    public readonly List<NCAgentMedicalScan> History = history;
}

[Serializable, NetSerializable]
public sealed class NCAgentMedTekClearMessage : CartridgeMessageEvent;

/// <summary>
/// Local server event raised once after the vanilla health analyzer completes a scan.
/// </summary>
[ByRefEvent]
public record struct NCAgentMedicalScanCompletedEvent(EntityUid User, EntityUid Target);
