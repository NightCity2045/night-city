// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.NCUI;
using Robust.Server.GameObjects;
using System.Numerics;

namespace Content.Server._NC.NCUI;

public readonly record struct NcUiActionContext(
    EntityUid UiOwner,
    Enum UiKey,
    EntityUid Actor,
    NcUiPermission RequiredPermission,
    NcUiRateLimitChannel RateChannel,
    int PayloadUnits = 1);

/// <summary>
/// Performs common authoritative barriers before a domain system validates physical gameplay constraints.
/// </summary>
public sealed partial class NcUiValidationSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NcUiSessionSystem _sessions = default!;
    [Dependency] private NcUiRateLimitSystem _rates = default!;

    public NcUiActionResult Validate(
        in NcUiActionContext context,
        NcUiActionMessage message,
        out NcUiSession session,
        out NcUiServerParticipant participant)
    {
        session = default!;
        participant = default!;
        if (!Exists(context.UiOwner) || !Exists(context.Actor) ||
            !_ui.IsUiOpen(context.UiOwner, context.UiKey, context.Actor))
        {
            return NcUiActionResult.RejectedUiClosed;
        }
        if (!_sessions.TryGetSession(message.Session, out session) || session.Owner != context.UiOwner ||
            !Equals(session.UiKey, context.UiKey))
        {
            return NcUiActionResult.RejectedInvalidSession;
        }
        if (!session.Participants.TryGetValue(context.Actor, out var foundParticipant))
            return NcUiActionResult.RejectedInvalidSession;
        participant = foundParticipant;
        if ((participant.Permissions & context.RequiredPermission) != context.RequiredPermission)
            return NcUiActionResult.RejectedNoPermission;
        if (context.PayloadUnits < 0 || context.PayloadUnits > session.Options.MaximumPayloadUnits)
            return NcUiActionResult.RejectedPayloadTooLarge;
        if (context.RateChannel == NcUiRateLimitChannel.Action && message.BaseRevision != session.Revision)
            return NcUiActionResult.RejectedOutdatedState;
        if (context.RateChannel == NcUiRateLimitChannel.Continuous &&
            !IsRevisionWithinWindow(message.BaseRevision, session.Revision, session.Options.PatchHistoryLength))
        {
            return NcUiActionResult.RejectedOutdatedState;
        }
        if (!message.Sequence.IsAfter(participant.LastSequence))
            return NcUiActionResult.RejectedDuplicateSequence;
        var rate = session.Options.GetRate(context.RateChannel);
        if (!_rates.TryConsume(context.Actor, session.Id, context.RateChannel, rate))
            return NcUiActionResult.RejectedRateLimited;
        // Sequences are consumed even when the domain rejects the action, preventing replay of a checked envelope.
        participant.LastSequence = message.Sequence;
        return NcUiActionResult.Accepted;
    }

    public NcUiActionResult ValidateContinuousUpdate(
        in NcUiActionContext context,
        NcUiUpdateOperationMessage message,
        int maximumPoints,
        float minimumQuantum,
        float maximumQuantum,
        out NcUiSession session,
        out NcUiServerParticipant participant,
        out Vector2[] points)
    {
        points = [];
        session = default!;
        participant = default!;
        if (maximumPoints <= 0 || !float.IsFinite(minimumQuantum) || !float.IsFinite(maximumQuantum) ||
            minimumQuantum <= 0f || maximumQuantum < minimumQuantum)
        {
            return NcUiActionResult.RejectedInvalidOperation;
        }
        if (message.Points.Length == 0 || message.Points.Length > maximumPoints)
            return NcUiActionResult.RejectedPayloadTooLarge;

        var continuousContext = context with
        {
            RateChannel = NcUiRateLimitChannel.Continuous,
            PayloadUnits = message.Points.Length,
        };
        var result = Validate(continuousContext, message, out session, out participant);
        if (result != NcUiActionResult.Accepted)
            return result;
        if (!float.IsFinite(message.Origin.X) || !float.IsFinite(message.Origin.Y) ||
            !float.IsFinite(message.PointQuantum) || message.PointQuantum < minimumQuantum ||
            message.PointQuantum > maximumQuantum)
        {
            return NcUiActionResult.RejectedInvalidOperation;
        }

        points = message.DecompressPoints();
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                points = [];
                return NcUiActionResult.RejectedInvalidOperation;
            }
        }
        return NcUiActionResult.Accepted;
    }

    private static bool IsRevisionWithinWindow(NcUiRevision candidate, NcUiRevision current, int window)
    {
        var cursor = candidate;
        for (var i = 0; i <= window; i++)
        {
            if (cursor == current)
                return true;
            cursor = cursor.Next();
        }
        return false;
    }
}
