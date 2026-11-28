// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Vehicle;

/// <summary>Someone finished putting the person they pull into a passenger seat.</summary>
[Serializable, NetSerializable]
public sealed partial class NCVehicleForceEnterDoAfterEvent : SimpleDoAfterEvent;

/// <summary>Someone outside finished pulling an occupant out of the vehicle.</summary>
[Serializable, NetSerializable]
public sealed partial class NCVehiclePullOutDoAfterEvent : SimpleDoAfterEvent;
