// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.NCUI;

/// <summary>
/// Identifies one authoritative NCUI session. Zero is reserved for an unopened session.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NcUiSessionId(uint Value)
{
    public static readonly NcUiSessionId Invalid = new(0);

    public bool IsValid => Value != 0;
}

/// <summary>
/// Monotonic version of the semantic state exposed by one session.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NcUiRevision(uint Value)
{
    public static readonly NcUiRevision Initial = new(1);

    public NcUiRevision Next()
    {
        var value = unchecked(Value + 1);
        return new NcUiRevision(value == 0 ? 1u : value);
    }

    public bool IsImmediatelyAfter(NcUiRevision previous) => this == previous.Next();
    public bool IsAfter(NcUiRevision previous) => Value != 0 && unchecked((int) (Value - previous.Value)) > 0;
}

/// <summary>
/// Client-generated sequence used to reject duplicate or replayed actions.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NcUiActionSequence(uint Value)
{
    public static readonly NcUiActionSequence Initial = new(1);

    public NcUiActionSequence Next()
    {
        var value = unchecked(Value + 1);
        return new NcUiActionSequence(value == 0 ? 1u : value);
    }

    public bool IsAfter(NcUiActionSequence previous) =>
        Value != 0 && unchecked((int) (Value - previous.Value)) > 0;
}

/// <summary>
/// Stable semantic object identity shared by participants. It is intentionally separate from client-only node IDs.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NcUiObjectId(ulong Value)
{
    public static readonly NcUiObjectId Invalid = new(0);

    public bool IsValid => Value != 0;
}

/// <summary>
/// Identifies one continuous operation such as a stroke or dragged instrument path.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NcUiOperationId(uint Value)
{
    public static readonly NcUiOperationId Invalid = new(0);

    public bool IsValid => Value != 0;
}

/// <summary>
/// Framework-level capabilities. Domain interfaces may combine these flags with their own server-side policy.
/// </summary>
[Flags]
[Serializable, NetSerializable]
public enum NcUiPermission : ulong
{
    None = 0,
    View = 1UL << 0,
    Interact = 1UL << 1,
    Edit = 1UL << 2,
    Operate = 1UL << 3,
    ManageParticipants = 1UL << 4,
    OverrideLeases = 1UL << 5,
    Debug = 1UL << 6,
}

/// <summary>
/// Defines how simultaneous mutations of a semantic object are arbitrated.
/// </summary>
[Serializable, NetSerializable]
public enum NcUiConcurrencyMode : byte
{
    Exclusive,
    Optimistic,
    AppendOnly,
}

/// <summary>
/// Common action outcome understood by every NCUI presenter.
/// </summary>
[Serializable, NetSerializable]
public enum NcUiActionResult : byte
{
    Accepted,
    AcceptedWithCorrection,
    RejectedOutdatedState,
    RejectedNoPermission,
    RejectedInvalidTarget,
    RejectedMissingItem,
    RejectedRateLimited,
    RejectedConflict,
    RejectedInvalidSession,
    RejectedDuplicateSequence,
    RejectedPayloadTooLarge,
    RejectedUiClosed,
    RejectedInvalidOperation,
}

/// <summary>
/// Independent traffic classes prevent continuous input from starving discrete actions or resync requests.
/// </summary>
[Serializable, NetSerializable]
public enum NcUiRateLimitChannel : byte
{
    Action,
    Continuous,
    Resync,
}
