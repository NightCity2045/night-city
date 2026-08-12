// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Targeting;

/// <summary>
/// Owns the private, server-authoritative body-region selection used by later action resolution.
/// </summary>
public sealed partial class NCTargetingSelectionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<NCTargetingSelectionRequest>(OnSelectionRequest);
        SubscribeNetworkEvent<NCTargetingSelectionQuery>(OnSelectionQuery);
    }

    /// <summary>
    /// Returns the current authoritative state. Bodies without an explicit selection use automatic targeting.
    /// </summary>
    public NCTargetingState GetSelection(EntityUid user)
    {
        if (!TryComp<NCTargetingSelectionComponent>(user, out var targeting) ||
            targeting.Mode != NCTargetingMode.SelectedZone ||
            targeting.SelectedZone is not { } selectedZone)
        {
            return new NCTargetingState(NCTargetingMode.Automatic, null);
        }

        return new NCTargetingState(NCTargetingMode.SelectedZone, selectedZone);
    }

    /// <summary>
    /// Validates and applies a selection to one controlled body. Invalid requests never mutate existing state.
    /// </summary>
    public bool TrySetSelection(
        EntityUid user,
        NCTargetingState requested,
        out NCTargetingState authoritative,
        out NCTargetingSelectionFailure failure)
    {
        authoritative = GetSelection(user);
        failure = NCTargetingSelectionFailure.None;

        if (!TryComp<NCMedicalBodyComponent>(user, out var anatomy) ||
            !_anatomy.TryGetProfile((user, anatomy), out var profile))
        {
            failure = NCTargetingSelectionFailure.AnatomyUnavailable;
            return false;
        }

        NCTargetingState normalized;
        switch (requested.Mode)
        {
            case NCTargetingMode.Automatic:
                // Validate profile integrity here; the later action resolves organs against the actual victim.
                if (!profile.TargetZones.Contains(profile.AutomaticFallbackZone) ||
                    !_prototypes.HasIndex(profile.AutomaticFallbackZone))
                {
                    failure = NCTargetingSelectionFailure.AnatomyIncomplete;
                    return false;
                }

                normalized = new NCTargetingState(NCTargetingMode.Automatic, null);
                break;

            case NCTargetingMode.SelectedZone:
                if (requested.SelectedZone is not { } requestedZone)
                {
                    failure = NCTargetingSelectionFailure.ZoneRequired;
                    return false;
                }

                if (!profile.TargetZones.Contains(requestedZone))
                {
                    failure = NCTargetingSelectionFailure.ZoneNotInProfile;
                    return false;
                }

                if (!_prototypes.HasIndex(requestedZone))
                {
                    failure = NCTargetingSelectionFailure.ZonePrototypeUnavailable;
                    return false;
                }

                normalized = new NCTargetingState(NCTargetingMode.SelectedZone, requestedZone);
                break;

            default:
                failure = NCTargetingSelectionFailure.InvalidMode;
                return false;
        }

        var targeting = EnsureComp<NCTargetingSelectionComponent>(user);
        targeting.Mode = normalized.Mode;
        targeting.SelectedZone = normalized.SelectedZone;
        unchecked
        {
            targeting.Revision++;
        }
        authoritative = normalized;
        return true;
    }

    private void OnSelectionRequest(NCTargetingSelectionRequest request, EntitySessionEventArgs args)
    {
        var authoritative = new NCTargetingState(NCTargetingMode.Automatic, null);
        var failure = NCTargetingSelectionFailure.NoControlledEntity;
        var accepted = false;

        // Never trust an entity supplied by a client. The sending session is the sole source of ownership.
        if (args.SenderSession.AttachedEntity is { Valid: true } user && Exists(user))
            accepted = TrySetSelection(user, request.State, out authoritative, out failure);

        // Directed delivery keeps the exact aimed region private from every other connected player.
        SendAck(args, request.RequestId, accepted, authoritative, failure);
    }

    private void OnSelectionQuery(NCTargetingSelectionQuery request, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } user ||
            !Exists(user))
        {
            SendAck(
                args,
                request.RequestId,
                false,
                new NCTargetingState(NCTargetingMode.Automatic, null),
                NCTargetingSelectionFailure.NoControlledEntity);
            return;
        }

        SendAck(
            args,
            request.RequestId,
            true,
            GetSelection(user),
            NCTargetingSelectionFailure.None);
    }

    private void SendAck(
        EntitySessionEventArgs args,
        uint requestId,
        bool accepted,
        NCTargetingState authoritative,
        NCTargetingSelectionFailure failure)
    {
        // This overload addresses exactly one session; never replace it with a broadcast.
        RaiseNetworkEvent(
            new NCTargetingSelectionAck(requestId, accepted, authoritative, failure),
            args.SenderSession);
    }
}
