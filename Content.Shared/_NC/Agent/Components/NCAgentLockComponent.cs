// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Agent.Components;

/// <summary>
/// Stores the server-only security state of one physical Agent.
/// The hash and salt deliberately never use networked fields.
/// </summary>
[RegisterComponent]
public sealed partial class NCAgentLockComponent : Component
{
    [DataField]
    public byte[] PinHash = [];

    [DataField]
    public byte[] PinSalt = [];

    [DataField]
    public int FailedAttempts;

    [DataField]
    public TimeSpan LockoutEnd;
}

/// <summary>
/// Lets other Agent-hosted systems ask the authoritative server whether a user unlocked the device.
/// </summary>
[ByRefEvent]
public record struct NCAgentDeviceAccessAttemptEvent(EntityUid User, bool Cancelled = false);
