// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Content.Shared.PDA;
using Robust.Shared.Player;

namespace Content.Server._NC.Agent;

/// <summary>
/// Owns Agent availability policy, privacy operations, groups, and actionable HUD notifications.
/// Database mutations remain authoritative and UI messages are treated only as requests.
/// </summary>
public sealed partial class NCAgentSystem
{
    private readonly Dictionary<Guid, PendingAgentNotification> _pendingNotifications = [];

    private void InitializeCommunication()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentRequestContactExchangeMessage>(OnRequestContactExchange);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSetPresenceMessage>(OnSetPresence);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSetNotificationSettingsMessage>(OnSetNotificationSettings);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSetBlockedMessage>(OnSetBlocked);
        SubscribeLocalEvent<NCAgentComponent, NCAgentClearConversationMessage>(OnClearConversation);
        SubscribeLocalEvent<NCAgentComponent, NCAgentCreateGroupMessage>(OnCreateGroup);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSelectGroupMessage>(OnSelectGroup);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSendGroupMessage>(OnSendGroupMessage);
        SubscribeLocalEvent<NCAgentComponent, NCAgentInviteGroupMemberMessage>(OnInviteGroupMember);
        SubscribeLocalEvent<NCAgentComponent, NCAgentRespondGroupInviteMessage>(OnRespondGroupInvite);
        SubscribeLocalEvent<NCAgentComponent, NCAgentRemoveGroupMemberMessage>(OnRemoveGroupMember);
        SubscribeLocalEvent<NCAgentComponent, NCAgentLeaveGroupMessage>(OnLeaveGroup);
        SubscribeLocalEvent<NCAgentComponent, NCAgentClearGroupConversationMessage>(OnClearGroupConversation);
        SubscribeNetworkEvent<NCAgentNotificationActionEvent>(OnNotificationAction);
    }

    private void OnRequestContactExchange(
        EntityUid uid,
        NCAgentComponent component,
        NCAgentRequestContactExchangeMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.NearbyShareIds.Contains(args.CharacterId) ||
            !TryFindNearbyAgentHolder(user, new NCCharacterId(args.CharacterId), out _) ||
            !_processing.Add((uid, user)))
            return;

        _ = ShareContactFromUiAsync(uid, user, view, new NCCharacterId(args.CharacterId));
    }

    private async Task ShareContactFromUiAsync(
        EntityUid agent,
        EntityUid user,
        AgentView view,
        NCCharacterId recipient)
    {
        try
        {
            view.Notice = await ShareContactAsync(user, view.Owner, recipient)
                ? "CONTACT REQUEST SENT"
                : "ADDRESS UNAVAILABLE";
        }
        catch (Exception exception)
        {
            HandleDatabaseError(agent, user, "request contact exchange", exception);
        }
        finally
        {
            FinishOperation(agent, user);
            await SendViewAsync(agent, user);
        }
    }

    private async Task<bool> ShareContactAsync(EntityUid performer, NCCharacterId sender, NCCharacterId recipient)
    {
        if (await _database.AreNCAgentProfilesBlockedAsync(sender, recipient) ||
            !TryResolveOnlineAgent(recipient, out _, out var recipientHolder))
        {
            NotifyCallHolder(performer, "ADDRESS UNAVAILABLE");
            return false;
        }

        var senderProfile = await _database.GetOrCreateNCAgentProfileAsync(sender, Configuration.AddressPrefix);
        if (senderProfile == null)
            return false;

        var id = Guid.NewGuid();
        _pendingNotifications[id] = new PendingAgentNotification(
            recipient,
            NCAgentNotificationKind.ContactRequest,
            NCAgentApp.Home,
            sender,
            null,
            DateTime.UtcNow + Configuration.ContactRequestTimeout);

        await SendAgentNotificationAsync(
            recipient,
            NCAgentNotificationKind.ContactRequest,
            "CONTACT EXCHANGE",
            $"{senderProfile.Alias} requests mutual contact exchange",
            true,
            id,
            forceOverlay: true,
            knownHolder: recipientHolder);
        NotifyCallHolder(performer, "CONTACT REQUEST SENT");
        return true;
    }

    private async void OnNotificationAction(NCAgentNotificationActionEvent message, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } user ||
            !_pendingNotifications.Remove(message.NotificationId, out var pending) ||
            pending.ExpiresAt <= DateTime.UtcNow ||
            !TryFindEquippedAgent(user, pending.Recipient, out var agent))
            return;

        var owner = pending.Recipient;

        if (message.Action == NCAgentNotificationAction.Accept)
        {
            switch (pending.Kind)
            {
                case NCAgentNotificationKind.ContactRequest when pending.Peer is { } peer:
                    if (!await _database.AreNCAgentProfilesBlockedAsync(owner, peer))
                        await _database.AddNCAgentMutualContactsAsync(owner, peer, DateTime.UtcNow);
                    break;
                case NCAgentNotificationKind.GroupInvite when pending.GroupId is { } groupId:
                    await _database.RespondNCAgentGroupInviteAsync(
                        owner,
                        groupId,
                        true,
                        DateTime.UtcNow,
                        Configuration.MaximumGroupMembers);
                    break;
            }
        }

        else if (message.Action == NCAgentNotificationAction.Decline &&
                 pending.Kind == NCAgentNotificationKind.GroupInvite &&
                 pending.GroupId is { } declinedGroupId)
        {
            await _database.RespondNCAgentGroupInviteAsync(
                owner,
                declinedGroupId,
                false,
                DateTime.UtcNow,
                Configuration.MaximumGroupMembers);
        }

        if (message.Action == NCAgentNotificationAction.Open)
        {
            _ui.TryOpenUi(agent, PdaUiKey.Key, user);
            if (_views.TryGetValue((agent, user), out var view))
            {
                view.ActiveApp = pending.App;
                view.SelectedCharacterId = pending.Peer;
                view.SelectedGroupId = pending.GroupId;
            }
        }

        await RefreshOpenViewsAsync();
    }

    private async void OnSetPresence(EntityUid uid, NCAgentComponent component, NCAgentSetPresenceMessage args)
    {
        if (!Enum.IsDefined(args.Presence) ||
            !TryGetView(uid, args.Actor, out var user, out var view) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.UpdateNCAgentPresenceAsync(view.Owner, args.Presence))
                view.Notice = "STATUS UPDATED";
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "update presence", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnSetNotificationSettings(
        EntityUid uid,
        NCAgentComponent component,
        NCAgentSetNotificationSettingsMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) || !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.UpdateNCAgentNotificationSettingsAsync(
                    view.Owner,
                    args.OverlayEnabled,
                    args.SoundEnabled))
                view.Notice = "NOTIFICATION SETTINGS UPDATED";
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "update notification settings", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await SendViewAsync(uid, user);
        }
    }

    private async void OnSetBlocked(EntityUid uid, NCAgentComponent component, NCAgentSetBlockedMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            args.CharacterId == view.Owner.Value ||
            (!view.ContactIds.Contains(args.CharacterId) &&
             !view.ConversationIds.Contains(args.CharacterId) &&
             !view.BlockedIds.Contains(args.CharacterId)) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            var target = new NCCharacterId(args.CharacterId);
            if (await _database.SetNCAgentBlockedAsync(view.Owner, target, args.Blocked, DateTime.UtcNow))
            {
                if (args.Blocked)
                {
                    view.SelectedCharacterId = null;
                    if (_calls.TryGetValue(view.Owner, out var call) &&
                        (call.Caller == target || call.Target == target))
                        EndCall(call, "ADDRESS UNAVAILABLE", NCAgentCallOutcome.SignalLost);
                }

                view.Notice = args.Blocked ? "ADDRESS BLOCKED" : "ADDRESS UNBLOCKED";
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "change block state", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnClearConversation(EntityUid uid, NCAgentComponent component, NCAgentClearConversationMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            (!view.ContactIds.Contains(args.CharacterId) && !view.ConversationIds.Contains(args.CharacterId)) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.ClearNCAgentConversationAsync(
                    view.Owner,
                    new NCCharacterId(args.CharacterId),
                    DateTime.UtcNow))
                view.Notice = "LOCAL HISTORY CLEARED";
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "clear local conversation", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await SendViewAsync(uid, user);
        }
    }

    private async void OnCreateGroup(EntityUid uid, NCAgentComponent component, NCAgentCreateGroupMessage args)
    {
        var name = args.Name.Trim();
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            name.Length is < 1 || name.Length > Configuration.MaximumGroupNameLength ||
            args.MemberCharacterIds.Any(id => !view.ContactIds.Contains(id)) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            var invitees = args.MemberCharacterIds
                .Distinct()
                .Take(Configuration.MaximumGroupMembers - 1)
                .Select(id => new NCCharacterId(id))
                .ToArray();
            var now = DateTime.UtcNow;
            var group = await _database.CreateNCAgentGroupAsync(
                view.Owner,
                name,
                invitees,
                now,
                now + Configuration.GroupInviteRetention,
                Configuration.MaximumGroupNameLength,
                Configuration.MaximumGroupMembers);
            if (group == null)
                return;

            view.SelectedGroupId = group.GroupId;
            view.SelectedCharacterId = null;
            view.ActiveApp = NCAgentApp.Messages;
            view.Notice = "GROUP CREATED";
            foreach (var invitee in group.InvitedCharacterIds)
                await SendGroupInviteNotificationAsync(invitee, view.Owner, group.GroupId, group.Name);
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "create group", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnSelectGroup(EntityUid uid, NCAgentComponent component, NCAgentSelectGroupMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) || !view.GroupIds.Contains(args.GroupId))
            return;

        view.SelectedGroupId = args.GroupId;
        view.SelectedCharacterId = null;
        view.ActiveApp = NCAgentApp.Messages;
        view.Notice = string.Empty;
        await SendViewAsync(uid, user);
    }

    private async void OnSendGroupMessage(EntityUid uid, NCAgentComponent component, NCAgentSendGroupMessage args)
    {
        var body = args.Body.Trim();
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            view.SelectedGroupId != args.GroupId || !view.GroupIds.Contains(args.GroupId) ||
            body.Length is < 1 || body.Length > Configuration.MaximumMessageLength)
            return;

        if (!CanUseMessageService(view.Owner, out var failure))
        {
            view.Notice = failure;
            await SendViewAsync(uid, user);
            return;
        }

        if (!_processing.Add((uid, user)))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var message = await _database.CreateNCAgentGroupMessageAsync(
                view.Owner,
                args.GroupId,
                body,
                now,
                now + Configuration.MessageRetention,
                Configuration.MaximumMessageLength);
            if (message == null)
                return;

            view.Notice = "GROUP MESSAGE SENT";
            var recipients = await _database.GetNCAgentGroupRecipientIdsAsync(args.GroupId, view.Owner);
            foreach (var recipient in recipients)
            {
                await SendAgentNotificationAsync(
                    recipient,
                    NCAgentNotificationKind.GroupMessage,
                    "GROUP MESSAGE",
                    $"New message in group from {message.SenderAlias}",
                    false,
                    groupId: args.GroupId,
                    app: NCAgentApp.Messages);
                await RefreshOwnerViewsAsync(recipient);
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "send group message", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await SendViewAsync(uid, user);
        }
    }

    private async void OnInviteGroupMember(EntityUid uid, NCAgentComponent component, NCAgentInviteGroupMemberMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.GroupIds.Contains(args.GroupId) || !view.ContactIds.Contains(args.CharacterId) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            var invitee = new NCCharacterId(args.CharacterId);
            var now = DateTime.UtcNow;
            if (await _database.InviteNCAgentGroupMemberAsync(
                    view.Owner,
                    args.GroupId,
                    invitee,
                    now,
                    now + Configuration.GroupInviteRetention,
                    Configuration.MaximumGroupMembers))
            {
                view.Notice = "GROUP INVITE SENT";
                await SendGroupInviteNotificationAsync(
                    invitee,
                    view.Owner,
                    args.GroupId,
                    view.GroupNames.GetValueOrDefault(args.GroupId, "GROUP"));
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "invite group member", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnRespondGroupInvite(EntityUid uid, NCAgentComponent component, NCAgentRespondGroupInviteMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) || !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.RespondNCAgentGroupInviteAsync(
                    view.Owner,
                    args.GroupId,
                    args.Accept,
                    DateTime.UtcNow,
                    Configuration.MaximumGroupMembers))
            {
                if (args.Accept)
                    view.SelectedGroupId = args.GroupId;
                view.Notice = args.Accept ? "GROUP JOINED" : "INVITE DECLINED";
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "respond to group invite", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnRemoveGroupMember(EntityUid uid, NCAgentComponent component, NCAgentRemoveGroupMemberMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.GroupIds.Contains(args.GroupId) || !view.GroupMemberIds.Contains(args.CharacterId) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.RemoveNCAgentGroupMemberAsync(
                    view.Owner,
                    args.GroupId,
                    new NCCharacterId(args.CharacterId)))
                view.Notice = "MEMBER REMOVED";
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "remove group member", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnLeaveGroup(EntityUid uid, NCAgentComponent component, NCAgentLeaveGroupMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.GroupIds.Contains(args.GroupId) || !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.LeaveNCAgentGroupAsync(view.Owner, args.GroupId, DateTime.UtcNow))
            {
                view.SelectedGroupId = null;
                view.Notice = "GROUP LEFT";
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "leave group", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnClearGroupConversation(
        EntityUid uid,
        NCAgentComponent component,
        NCAgentClearGroupConversationMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.GroupIds.Contains(args.GroupId) || !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.ClearNCAgentGroupConversationAsync(view.Owner, args.GroupId, DateTime.UtcNow))
                view.Notice = "LOCAL GROUP HISTORY CLEARED";
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "clear local group history", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await SendViewAsync(uid, user);
        }
    }

    private async Task SendGroupInviteNotificationAsync(
        NCCharacterId recipient,
        NCCharacterId inviter,
        long groupId,
        string groupName)
    {
        var inviterProfile = await _database.GetOrCreateNCAgentProfileAsync(inviter, Configuration.AddressPrefix);
        if (inviterProfile == null)
            return;

        var id = Guid.NewGuid();
        _pendingNotifications[id] = new PendingAgentNotification(
            recipient,
            NCAgentNotificationKind.GroupInvite,
            NCAgentApp.Messages,
            inviter,
            groupId,
            DateTime.UtcNow + Configuration.GroupInviteRetention);
        await SendAgentNotificationAsync(
            recipient,
            NCAgentNotificationKind.GroupInvite,
            "GROUP INVITATION",
            $"{inviterProfile.Alias} invited you to {groupName}",
            true,
            id,
            groupId: groupId,
            app: NCAgentApp.Messages);
    }

    private async Task SendAgentNotificationAsync(
        NCCharacterId recipient,
        NCAgentNotificationKind kind,
        string title,
        string detail,
        bool actionable,
        Guid? notificationId = null,
        NCCharacterId? peer = null,
        long? groupId = null,
        NCAgentApp app = NCAgentApp.Home,
        bool forceOverlay = false,
        EntityUid? knownHolder = null)
    {
        var profile = await _database.GetOrCreateNCAgentProfileAsync(recipient, Configuration.AddressPrefix);
        if (profile == null || profile.Presence is NCAgentPresenceStatus.DoNotDisturb or NCAgentPresenceStatus.Offline)
            return;

        if (!TryResolveOnlineAgent(recipient, out var agent, out var holder))
            return;

        if (knownHolder is { } expectedHolder && expectedHolder != holder)
            return;

        var id = notificationId ?? Guid.NewGuid();
        if (!actionable)
        {
            _pendingNotifications[id] = new PendingAgentNotification(
                recipient,
                kind,
                app,
                peer,
                groupId,
                DateTime.UtcNow + Configuration.NotificationOverlayDuration);
        }

        if (profile.NotificationSoundEnabled)
            _ringer.RingerPlayRingtone(agent);
        if ((profile.OverlayNotificationsEnabled || forceOverlay) && _players.TryGetSessionByEntity(holder, out var session))
            RaiseNetworkEvent(new NCAgentNotificationEvent(id, kind, title, detail, actionable), session);
    }

    private NCAgentNetworkState ResolveNetworkState(NCCharacterId owner)
    {
        if (!Configuration.ServiceAvailable)
            return NCAgentNetworkState.ServiceUnavailable;
        if (!TryResolveOnlineAgent(owner, out _, out var holder))
            return NCAgentNetworkState.Disconnected;
        return ResolveNetworkState(holder);
    }

    private NCAgentNetworkState ResolveNetworkState(EntityUid holder)
    {
        if (!Configuration.ServiceAvailable)
            return NCAgentNetworkState.ServiceUnavailable;

        var grid = Transform(holder).GridUid;
        return grid is { } gridUid && TryComp<NCAgentNetworkCoverageComponent>(gridUid, out var coverage)
            ? coverage.State
            : Configuration.DefaultNetworkState;
    }

    private bool CanUseMessageService(NCCharacterId owner, out string failure)
    {
        var state = ResolveNetworkState(owner);
        var allowed = state switch
        {
            NCAgentNetworkState.Online => true,
            NCAgentNetworkState.Degraded => Configuration.DegradedAllowsMessages,
            NCAgentNetworkState.LocalOnly => true,
            _ => false,
        };
        failure = allowed ? string.Empty : "CITINET SERVICE UNAVAILABLE";
        return allowed;
    }

    private bool CanUseCitiNetService(NCCharacterId owner, out string failure)
    {
        var state = ResolveNetworkState(owner);
        var allowed = state == NCAgentNetworkState.Online ||
                      state == NCAgentNetworkState.Degraded && Configuration.DegradedAllowsCitiNet;
        failure = allowed ? string.Empty : "CITINET FEED UNAVAILABLE";
        return allowed;
    }

    /// <summary>
    /// Direct messages may queue for an offline recipient, while local-only coverage can only route
    /// to another currently attached endpoint on the same named city network.
    /// </summary>
    private bool CanRouteMessage(NCCharacterId sender, NCCharacterId recipient, out string failure)
    {
        if (!CanUseMessageService(sender, out failure))
            return false;

        var senderState = ResolveNetworkState(sender);
        var recipientState = ResolveNetworkState(recipient);
        if (senderState != NCAgentNetworkState.LocalOnly && recipientState != NCAgentNetworkState.LocalOnly)
            return true;

        if (!TryResolveOnlineAgent(sender, out _, out var senderHolder) ||
            !TryResolveOnlineAgent(recipient, out _, out var recipientHolder) ||
            ResolveNetworkId(senderHolder) != ResolveNetworkId(recipientHolder))
        {
            failure = "LOCAL NETWORK ROUTE UNAVAILABLE";
            return false;
        }

        return true;
    }

    private bool CanConnectCall(EntityUid callerHolder, EntityUid targetHolder, out string failure)
    {
        var callerState = ResolveNetworkState(callerHolder);
        var targetState = ResolveNetworkState(targetHolder);
        var available = callerState switch
        {
            NCAgentNetworkState.Online => true,
            NCAgentNetworkState.Degraded => Configuration.DegradedAllowsCalls,
            NCAgentNetworkState.LocalOnly => true,
            _ => false,
        } && targetState switch
        {
            NCAgentNetworkState.Online => true,
            NCAgentNetworkState.Degraded => Configuration.DegradedAllowsCalls,
            NCAgentNetworkState.LocalOnly => true,
            _ => false,
        };

        if (available &&
            (callerState == NCAgentNetworkState.LocalOnly || targetState == NCAgentNetworkState.LocalOnly))
            available = ResolveNetworkId(callerHolder) == ResolveNetworkId(targetHolder);

        failure = available ? string.Empty : "CALL NETWORK UNAVAILABLE";
        return available;
    }

    private string ResolveNetworkId(EntityUid holder)
    {
        var grid = Transform(holder).GridUid;
        return grid is { } gridUid && TryComp<NCAgentNetworkCoverageComponent>(gridUid, out var coverage)
            ? coverage.NetworkId
            : "CITYNET";
    }

    private void UpdateCommunicationTimeouts()
    {
        if (_pendingNotifications.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var id in _pendingNotifications
                     .Where(entry => entry.Value.ExpiresAt <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
            _pendingNotifications.Remove(id);
    }

    private void ClearCommunication()
    {
        _pendingNotifications.Clear();
        ClearCitiNet();
    }

    private sealed record PendingAgentNotification(
        NCCharacterId Recipient,
        NCAgentNotificationKind Kind,
        NCAgentApp App,
        NCCharacterId? Peer,
        long? GroupId,
        DateTime ExpiresAt);
}
