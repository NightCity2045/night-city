// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Server._NC.Agent.Components;
using Content.Server._NC.City.Zones;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.PDA;
using Content.Server.PDA.Ringer;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Identity;
using Content.Shared.GameTicking;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    private static readonly ProtoId<NCAgentConfigurationPrototype> DefaultConfiguration = "Default";

    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private PdaSystem _pda = default!;
    [Dependency] private RingerSystem _ringer = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly Dictionary<(EntityUid Agent, EntityUid User), AgentView> _views = [];
    private readonly Dictionary<NCCharacterId, EntityUid> _boundAgents = [];
    private readonly HashSet<(EntityUid Agent, EntityUid User)> _processing = [];

    private NCAgentConfigurationPrototype Configuration => _prototypes.Index(DefaultConfiguration);

    private void InitializePersistence()
    {
        SubscribeLocalEvent<NCAgentComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCAgentComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCAgentComponent, ComponentShutdown>(OnAgentShutdown);
        SubscribeLocalEvent<NCAgentComponent, NCAgentOpenAppMessage>(OnOpenApp);
        SubscribeLocalEvent<NCAgentComponent, NCAgentDirectorySearchMessage>(OnDirectorySearch);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSetAliasMessage>(OnSetAlias);
        SubscribeLocalEvent<NCAgentComponent, NCAgentAddContactMessage>(OnAddContact);
        SubscribeLocalEvent<NCAgentComponent, NCAgentRemoveContactMessage>(OnRemoveContact);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSelectConversationMessage>(OnSelectConversation);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSendMessageMessage>(OnSendMessage);
        SubscribeLocalEvent<NCAgentComponent, NCAgentCreatePostMessage>(OnCreatePost);
        SubscribeLocalEvent<NCAgentComponent, NCAgentCreateCommentMessage>(OnCreateComment);
        SubscribeLocalEvent<NCAgentComponent, NCAgentToggleReactionMessage>(OnToggleReaction);
        SubscribeLocalEvent<NCAgentComponent, NCAgentDeletePostMessage>(OnDeletePost);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSetFeedOffsetMessage>(OnSetFeedOffset);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId) ||
            !TryFindEquippedAgent(args.Mob, characterId, out var agent))
            return;

        BindAgent(agent, characterId);

        try
        {
            await _database.GetOrCreateNCAgentProfileAsync(characterId, Configuration.AddressPrefix);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to initialize Agent profile for character {characterId.Value}: {exception}");
        }
    }

    private async void OnUiOpened(EntityUid uid, NCAgentComponent component, BoundUIOpenedEvent args)
    {
        if (!PdaUiKey.Key.Equals(args.UiKey) || args.Actor is not { Valid: true } user ||
            !TryGetOrBindOwner(uid, user, out var owner))
            return;

        var view = new AgentView(owner)
        {
            Authenticated = TryResumeUnlock(uid, user),
        };
        _views[(uid, user)] = view;
        SendLockState(uid, user);

        if (!view.Authenticated)
            return;

        await InitializeAuthenticatedViewAsync(uid, user, owner);
    }

    private async Task InitializeAuthenticatedViewAsync(EntityUid uid, EntityUid user, NCCharacterId owner)
    {
        try
        {
            var profile = await _database.GetOrCreateNCAgentProfileAsync(owner, Configuration.AddressPrefix);
            if (profile == null)
                return;

            var now = DateTime.UtcNow;
            await _database.PurgeExpiredNCAgentMessagesAsync(now);
            await _database.PurgeExpiredNCAgentGroupMessagesAsync(now);
            await _database.PurgeExpiredNCAgentGroupInvitesAsync(now);
            await _database.PurgeExpiredNCAgentCallLogsAsync(now - Configuration.CallHistoryRetention);
            await _database.PurgeExpiredNCCitiNetPostsAsync(now - Configuration.CitiNetPostRetention);
            await SendViewAsync(uid, user);
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "open Agent", exception);
        }
    }

    private void OnUiClosed(Entity<NCAgentComponent> ent, ref BoundUIClosedEvent args)
    {
        if (!PdaUiKey.Key.Equals(args.UiKey) || args.Actor is not { Valid: true } user)
            return;

        _views.Remove((ent.Owner, user));
        _processing.Remove((ent.Owner, user));
    }

    private async void OnOpenApp(EntityUid uid, NCAgentComponent component, NCAgentOpenAppMessage args)
    {
        if (!Enum.IsDefined(args.App) || !TryGetView(uid, args.Actor, out var user, out var view))
            return;

        view.ActiveApp = args.App;
        view.Notice = string.Empty;
        if (args.App == NCAgentApp.CitiNet)
            view.FeedOffset = 0;
        if (args.App == NCAgentApp.Map)
            SendMapSnapshot(uid, user);
        await SendViewAsync(uid, user);
    }

    /// <summary>
    /// Sends sanitized public cartography only for an explicit map-app request.
    /// Server-only zone prototypes and private zone kinds never cross the network boundary.
    /// </summary>
    private void SendMapSnapshot(EntityUid agent, EntityUid user)
    {
        if (!_mapCoordinates.TryGetCoordinates(user, out var coordinates))
        {
            _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentMapUpdateMessage(
                string.Empty,
                0,
                default,
                [],
                "CITY GRID SIGNAL UNAVAILABLE"), user);
            return;
        }

        var zoneInfos = new List<NCZoneInfo>();
        var geometryInfos = new List<NCZoneGeometryInfo>();
        var features = new List<NCAgentMapFeature>();
        _zones.GetZones(coordinates.NetworkId, zoneInfos);

        foreach (var zone in zoneInfos)
        {
            if (!TryGetPublicMapKind(zone.ContextSlot, out var featureKind) ||
                !_zones.GetGeometry(zone.Id, geometryInfos))
            {
                continue;
            }

            foreach (var geometry in geometryInfos)
            {
                switch (geometry.Kind)
                {
                    case NCZoneGeometryKind.Polygon when geometry.Global || geometry.Z == coordinates.Z:
                    case NCZoneGeometryKind.Volume when geometry.Global ||
                                                        coordinates.Z >= geometry.MinZ &&
                                                        coordinates.Z <= geometry.MaxZ:
                        if (geometry.Vertices.Count >= 3)
                        {
                            features.Add(new NCAgentMapFeature(
                                featureKind,
                                NCAgentMapGeometryKind.Polygon,
                                zone.Name,
                                geometry.Vertices.ToList(),
                                []));
                        }
                        break;
                    case NCZoneGeometryKind.TileMask:
                    {
                        var chunks = geometry.Chunks
                            .Where(chunk => chunk.Z == coordinates.Z)
                            .Select(chunk => new NCAgentMapTileChunk(
                                chunk.Origin.X,
                                chunk.Origin.Y,
                                chunk.Rows.ToList()))
                            .ToList();
                        if (chunks.Count > 0)
                        {
                            features.Add(new NCAgentMapFeature(
                                featureKind,
                                NCAgentMapGeometryKind.TileMask,
                                zone.Name,
                                [],
                                chunks));
                        }
                        break;
                    }
                }
            }
        }

        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentMapUpdateMessage(
            coordinates.NetworkId.ToString(),
            coordinates.Z,
            coordinates.Position,
            features,
            features.Count == 0 ? "SEMANTIC CARTOGRAPHY UNAVAILABLE // GRID ONLY" : string.Empty), user);
    }

    private static bool TryGetPublicMapKind(NCZoneContextSlot contextSlot, out NCAgentMapFeatureKind kind)
    {
        kind = contextSlot switch
        {
            NCZoneContextSlot.District => NCAgentMapFeatureKind.District,
            NCZoneContextSlot.Sector => NCAgentMapFeatureKind.Sector,
            NCZoneContextSlot.Street => NCAgentMapFeatureKind.Street,
            NCZoneContextSlot.Building => NCAgentMapFeatureKind.Building,
            _ => default,
        };
        return contextSlot is NCZoneContextSlot.District or
            NCZoneContextSlot.Sector or
            NCZoneContextSlot.Street or
            NCZoneContextSlot.Building;
    }

    private async void OnDirectorySearch(EntityUid uid, NCAgentComponent component, NCAgentDirectorySearchMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view))
            return;

        if (!CanUseCitiNetService(view.Owner, out var failure))
        {
            view.DirectoryQuery = string.Empty;
            view.Notice = failure;
            await SendViewAsync(uid, user);
            return;
        }

        var query = args.Query.Trim();
        if (query.Length > Configuration.MaximumDirectoryQueryLength)
            return;

        view.DirectoryQuery = query;
        view.ActiveApp = NCAgentApp.Contacts;
        view.Notice = string.Empty;
        await SendViewAsync(uid, user);
    }

    private async void OnSetAlias(EntityUid uid, NCAgentComponent component, NCAgentSetAliasMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view))
            return;

        var alias = NormalizeAlias(args.Alias);
        if (!IsValidAlias(alias))
        {
            view.Notice = $"HANDLE MUST BE {Configuration.MinimumAliasLength}-{Configuration.MaximumAliasLength} CHARACTERS";
            await SendViewAsync(uid, user);
            return;
        }

        if (!_processing.Add((uid, user)))
            return;

        try
        {
            var result = await _database.UpdateNCAgentAliasAsync(
                view.Owner,
                alias,
                alias.ToUpperInvariant());
            switch (result)
            {
                case NCAgentAliasUpdateResult.Updated:
                    view.Notice = "HANDLE UPDATED";
                    UpdateActiveCallAlias(view.Owner, alias);
                    await RefreshOpenViewsAsync();
                    break;
                case NCAgentAliasUpdateResult.Duplicate:
                    view.Notice = "HANDLE ALREADY IN USE";
                    await SendViewAsync(uid, user);
                    break;
                default:
                    view.Notice = "CITINET PROFILE UNAVAILABLE";
                    await SendViewAsync(uid, user);
                    break;
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "change CitiNet handle", exception);
            await SendViewAsync(uid, user);
        }
        finally
        {
            FinishOperation(uid, user);
        }
    }

    /// <summary>
    /// Canonicalizes user input before uniqueness checks so visually identical handles share one key.
    /// </summary>
    private static string NormalizeAlias(string input)
    {
        var source = input.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(source.Length);
        var previousWhitespace = false;
        foreach (var value in source)
        {
            if (char.IsWhiteSpace(value))
            {
                if (!previousWhitespace)
                    builder.Append(' ');
                previousWhitespace = true;
                continue;
            }

            previousWhitespace = false;
            builder.Append(value);
        }

        return builder.ToString();
    }

    private bool IsValidAlias(string alias)
    {
        var config = Configuration;
        if (alias.Length < config.MinimumAliasLength || alias.Length > config.MaximumAliasLength)
            return false;

        return alias.All(value => char.IsLetterOrDigit(value) || value is ' ' or '_' or '-' or '.');
    }

    private async void OnAddContact(EntityUid uid, NCAgentComponent component, NCAgentAddContactMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.VisibleDirectoryIds.Contains(args.CharacterId) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.AddNCAgentContactAsync(view.Owner, new NCCharacterId(args.CharacterId)))
            {
                view.SelectedCharacterId = new NCCharacterId(args.CharacterId);
                view.Notice = "CONTACT SAVED";
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "add contact", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await SendViewAsync(uid, user);
        }
    }

    private async void OnRemoveContact(EntityUid uid, NCAgentComponent component, NCAgentRemoveContactMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.ContactIds.Contains(args.CharacterId) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (await _database.RemoveNCAgentContactAsync(view.Owner, new NCCharacterId(args.CharacterId)))
            {
                if (view.SelectedCharacterId?.Value == args.CharacterId)
                    view.SelectedCharacterId = null;
                view.Notice = "CONTACT REMOVED";
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "remove contact", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await SendViewAsync(uid, user);
        }
    }

    private async void OnSelectConversation(EntityUid uid, NCAgentComponent component, NCAgentSelectConversationMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            (!view.ContactIds.Contains(args.CharacterId) && !view.ConversationIds.Contains(args.CharacterId)))
            return;

        view.SelectedCharacterId = new NCCharacterId(args.CharacterId);
        view.SelectedGroupId = null;
        view.ActiveApp = NCAgentApp.Messages;
        view.Notice = string.Empty;
        await SendViewAsync(uid, user);
    }

    private async void OnSendMessage(EntityUid uid, NCAgentComponent component, NCAgentSendMessageMessage args)
    {
        var body = args.Body.Trim();
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            view.SelectedCharacterId?.Value != args.RecipientCharacterId ||
            (!view.ContactIds.Contains(args.RecipientCharacterId) &&
             !view.ConversationIds.Contains(args.RecipientCharacterId)) ||
            body.Length is < 1 || body.Length > Configuration.MaximumMessageLength ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var recipient = new NCCharacterId(args.RecipientCharacterId);
            if (!CanRouteMessage(view.Owner, recipient, out var failure))
            {
                view.Notice = failure;
                return;
            }

            var message = await _database.CreateNCAgentMessageAsync(
                view.Owner,
                recipient,
                body,
                now,
                now + Configuration.MessageRetention);
            if (message == null)
            {
                view.Notice = "ADDRESS UNAVAILABLE";
                return;
            }

            view.Notice = "MESSAGE SENT";
            await SendAgentNotificationAsync(
                recipient,
                NCAgentNotificationKind.DirectMessage,
                "NEW MESSAGE",
                $"New encrypted message from {message.SenderAlias}",
                false,
                peer: view.Owner,
                app: NCAgentApp.Messages);
            await RefreshOwnerViewsAsync(recipient);
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "send message", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await SendViewAsync(uid, user);
        }
    }

    private async void OnCreatePost(EntityUid uid, NCAgentComponent component, NCAgentCreatePostMessage args)
    {
        var body = args.Body.Trim();
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            body.Length is < 1 || body.Length > Configuration.MaximumPostLength ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (!CanUseCitiNetService(view.Owner, out var failure))
            {
                view.Notice = failure;
                return;
            }

            if (!TryConsumeCitiNetCooldown(
                    view.Owner,
                    NCAgentCitiNetOperation.Post,
                    Configuration.CitiNetPostCooldown,
                    out var remaining))
            {
                view.Notice = CitiNetRateLimitNotice(remaining);
                return;
            }

            var post = await _database.CreateNCCitiNetPostAsync(view.Owner, body, DateTime.UtcNow);
            if (post != null)
            {
                view.FeedOffset = 0;
                view.Notice = "POST PUBLISHED";
                await NotifyCitiNetMentionsAsync(
                    view.Owner,
                    post.AuthorAlias,
                    body,
                    new HashSet<NCCharacterId>());
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "publish CitiNet post", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnCreateComment(EntityUid uid, NCAgentComponent component, NCAgentCreateCommentMessage args)
    {
        var body = args.Body.Trim();
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.PostIds.Contains(args.PostId) ||
            body.Length is < 1 || body.Length > Configuration.MaximumCommentLength ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (!CanUseCitiNetService(view.Owner, out var failure))
            {
                view.Notice = failure;
                return;
            }

            if (!TryConsumeCitiNetCooldown(
                    view.Owner,
                    NCAgentCitiNetOperation.Comment,
                    Configuration.CitiNetCommentCooldown,
                    out var remaining))
            {
                view.Notice = CitiNetRateLimitNotice(remaining);
                return;
            }

            var created = await _database.CreateNCCitiNetCommentAsync(
                view.Owner,
                args.PostId,
                body,
                DateTime.UtcNow);
            if (created != null)
            {
                view.Notice = "COMMENT PUBLISHED";
                var notified = new HashSet<NCCharacterId> { view.Owner };
                if (created.PostAuthorCharacterId != view.Owner)
                {
                    notified.Add(created.PostAuthorCharacterId);
                    await SendAgentNotificationAsync(
                        created.PostAuthorCharacterId,
                        NCAgentNotificationKind.CitiNetComment,
                        "NEW CITINET COMMENT",
                        $"{created.Comment.AuthorAlias} replied to your post",
                        false,
                        app: NCAgentApp.CitiNet);
                }

                await NotifyCitiNetMentionsAsync(view.Owner, created.Comment.AuthorAlias, body, notified);
            }
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "publish CitiNet comment", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnToggleReaction(EntityUid uid, NCAgentComponent component, NCAgentToggleReactionMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.PostIds.Contains(args.PostId) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (!CanUseCitiNetService(view.Owner, out var failure))
            {
                view.Notice = failure;
                return;
            }

            if (!TryConsumeCitiNetCooldown(
                    view.Owner,
                    NCAgentCitiNetOperation.Reaction,
                    Configuration.CitiNetReactionCooldown,
                    out var remaining))
            {
                view.Notice = CitiNetRateLimitNotice(remaining);
                return;
            }

            await _database.ToggleNCCitiNetReactionAsync(view.Owner, args.PostId, DateTime.UtcNow);
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "react to CitiNet post", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async Task SendViewAsync(EntityUid agent, EntityUid user)
    {
        if (!_views.TryGetValue((agent, user), out var view) ||
            !IsViewAuthenticated(agent, user, view) ||
            Deleted(agent) ||
            Deleted(user))
            return;

        var config = Configuration;
        var now = DateTime.UtcNow;
        if (view.ActiveApp == NCAgentApp.Messages && view.SelectedCharacterId is { } selectedConversation)
            await _database.MarkNCAgentConversationReadAsync(view.Owner, selectedConversation, now);
        if (view.ActiveApp == NCAgentApp.Messages && view.SelectedGroupId is { } selectedGroup)
            await _database.MarkNCAgentGroupReadAsync(view.Owner, selectedGroup, now);
        if (view.ActiveApp == NCAgentApp.Calls)
            await _database.MarkNCAgentCallHistoryReadAsync(view.Owner, now);

        var snapshot = await _database.GetNCAgentSnapshotAsync(
            view.Owner,
            view.SelectedCharacterId,
            view.SelectedGroupId,
            view.DirectoryQuery,
            now,
            config.DirectoryResultLimit,
            config.ConversationPeerLimit,
            config.ConversationMessageLimit,
            config.CallHistoryLimit,
            config.GroupConversationLimit,
            config.GroupMessageLimit,
            view.FeedOffset,
            view.ActiveApp == NCAgentApp.CitiNet ? config.FeedPostLimit : 0,
            config.FeedCommentLimit);
        if (snapshot == null || !_views.ContainsKey((agent, user)))
            return;

        IReadOnlyList<NCAgentProfileData> nearbyProfiles = Array.Empty<NCAgentProfileData>();
        if (view.ActiveApp == NCAgentApp.Contacts)
        {
            var nearbyOwners = FindNearbyAgentOwners(user, view.Owner);
            nearbyProfiles = await _database.GetNCAgentProfilesAsync(nearbyOwners);
        }

        if (!_views.ContainsKey((agent, user)))
            return;

        IReadOnlyList<NCCitiNetPostData> visiblePosts = snapshot.Posts;
        var feedHasOlderPosts = snapshot.FeedHasOlderPosts;
        if (!CanUseCitiNetService(view.Owner, out var feedFailure))
        {
            visiblePosts = Array.Empty<NCCitiNetPostData>();
            feedHasOlderPosts = false;
            if (view.ActiveApp == NCAgentApp.CitiNet)
                view.Notice = feedFailure;
        }

        view.ContactIds.Clear();
        view.ConversationIds.Clear();
        view.VisibleDirectoryIds.Clear();
        view.NearbyShareIds.Clear();
        view.BlockedIds.Clear();
        view.GroupIds.Clear();
        view.GroupNames.Clear();
        view.GroupMemberIds.Clear();
        view.PostIds.Clear();
        view.Targets.Clear();
        view.OwnAlias = snapshot.Profile.Alias;
        view.OwnAddress = snapshot.Profile.Address;

        foreach (var contact in snapshot.Contacts)
        {
            view.ContactIds.Add(contact.CharacterId.Value);
            view.Targets[contact.CharacterId] = (contact.Alias, contact.Address);
        }

        foreach (var conversation in snapshot.Conversations)
        {
            view.ConversationIds.Add(conversation.CharacterId.Value);
            view.Targets[conversation.CharacterId] = (conversation.Alias, conversation.Address);
        }

        foreach (var entry in snapshot.Directory)
        {
            view.VisibleDirectoryIds.Add(entry.CharacterId.Value);
            view.Targets[entry.CharacterId] = (entry.Alias, entry.Address);
        }

        foreach (var nearby in nearbyProfiles)
            view.NearbyShareIds.Add(nearby.CharacterId.Value);

        foreach (var blocked in snapshot.Blocked)
            view.BlockedIds.Add(blocked.CharacterId.Value);

        foreach (var group in snapshot.Groups)
        {
            view.GroupIds.Add(group.GroupId);
            view.GroupNames[group.GroupId] = group.Name;
        }

        foreach (var member in snapshot.GroupMembers)
            view.GroupMemberIds.Add(member.CharacterId.Value);

        foreach (var post in visiblePosts)
            view.PostIds.Add(post.PostId);

        if (view.SelectedCharacterId is { } selected &&
            !view.ContactIds.Contains(selected.Value) &&
            !view.ConversationIds.Contains(selected.Value))
            view.SelectedCharacterId = null;

        if (view.SelectedGroupId is { } selectedGroupId && !view.GroupIds.Contains(selectedGroupId))
            view.SelectedGroupId = null;

        var call = BuildCallSummary(view.Owner);
        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentUpdateMessage(
            view.ActiveApp,
            snapshot.Profile.CharacterId.Value,
            snapshot.Profile.Alias,
            snapshot.Profile.Address,
            snapshot.Profile.Presence,
            ResolveNetworkState(view.Owner),
            snapshot.Profile.OverlayNotificationsEnabled,
            snapshot.Profile.NotificationSoundEnabled,
            snapshot.Directory.Select(value => new NCAgentDirectoryEntry(
                value.CharacterId.Value,
                value.Alias,
                value.Address,
                value.IsContact,
                value.Presence,
                ResolveNetworkState(value.CharacterId))).ToList(),
            snapshot.Contacts.Select(value => new NCAgentContactSummary(
                value.CharacterId.Value,
                value.Alias,
                value.Address,
                value.Presence,
                ResolveNetworkState(value.CharacterId))).ToList(),
            nearbyProfiles
                .OrderBy(value => value.Alias)
                .Select(value => new NCAgentNearbyUserSummary(
                    value.CharacterId.Value,
                    value.Alias,
                    value.Address))
                .ToList(),
            snapshot.Blocked.Select(value => new NCAgentBlockedSummary(
                value.CharacterId.Value,
                value.Alias,
                value.Address)).ToList(),
            snapshot.Conversations.Select(value => new NCAgentConversationSummary(
                value.CharacterId.Value,
                value.Alias,
                value.Address,
                value.LastMessageAt,
                value.UnreadCount)).ToList(),
            view.SelectedCharacterId?.Value,
            snapshot.Messages.Select(value => new NCAgentMessageSummary(
                value.MessageId,
                value.SenderCharacterId.Value,
                value.SenderAlias,
                value.RecipientCharacterId.Value,
                value.RecipientAlias,
                value.Body,
                value.SentAt,
                value.SenderCharacterId == view.Owner)).ToList(),
            visiblePosts.Select(value => new NCAgentCitiNetPostSummary(
                value.PostId,
                value.AuthorCharacterId.Value,
                value.AuthorAlias,
                value.Body,
                value.CreatedAt,
                value.ReactionCount,
                value.ReactedByViewer,
                value.AuthorCharacterId == view.Owner,
                value.Comments.Select(comment => new NCAgentCitiNetCommentSummary(
                    comment.CommentId,
                    comment.AuthorCharacterId.Value,
                    comment.AuthorAlias,
                    comment.Body,
                    comment.CreatedAt)).ToList())).ToList(),
            view.FeedOffset,
            view.FeedOffset > 0,
            feedHasOlderPosts,
            call,
            snapshot.CallHistory.Select(value => new NCAgentCallHistorySummary(
                value.CallId,
                value.IsOutgoing,
                value.RemoteCharacterId.Value,
                value.RemoteAlias,
                value.RemoteAddress,
                value.StartedAt,
                value.AnsweredAt,
                value.EndedAt,
                value.Outcome,
                value.IsUnread)).ToList(),
            snapshot.Groups.Select(value => new NCAgentGroupSummary(
                value.GroupId,
                value.Name,
                value.OwnerAlias,
                value.Role,
                value.LastMessageAt,
                value.UnreadCount)).ToList(),
            view.SelectedGroupId,
            snapshot.GroupMembers.Select(value => new NCAgentGroupMemberSummary(
                value.CharacterId.Value,
                value.Alias,
                value.Address,
                value.Role,
                value.IsContact)).ToList(),
            snapshot.GroupMessages.Select(value => new NCAgentGroupMessageSummary(
                value.MessageId,
                value.GroupId,
                value.SenderCharacterId.Value,
                value.SenderAlias,
                value.Body,
                value.SentAt,
                value.SenderCharacterId == view.Owner)).ToList(),
            snapshot.GroupInvites.Select(value => new NCAgentGroupInviteSummary(
                value.GroupId,
                value.GroupName,
                value.InviterCharacterId.Value,
                value.InviterAlias,
                value.ExpiresAt)).ToList(),
            snapshot.UnreadMessageCount,
            snapshot.UnreadGroupMessageCount,
            snapshot.MissedCallCount,
            view.Notice), user);
    }

    private async Task RefreshOpenViewsAsync()
    {
        foreach (var key in _views.Keys.ToArray())
        {
            if (Deleted(key.Agent) || Deleted(key.User))
            {
                _views.Remove(key);
                _processing.Remove(key);
                continue;
            }

            await SendViewAsync(key.Agent, key.User);
        }
    }

    private async Task RefreshOwnerViewsAsync(NCCharacterId owner)
    {
        foreach (var key in _views
                     .Where(entry => entry.Value.Owner == owner)
                     .Select(entry => entry.Key)
                     .ToArray())
            await SendViewAsync(key.Agent, key.User);
    }

    private void FinishOperation(EntityUid agent, EntityUid user)
    {
        _processing.Remove((agent, user));
    }

    private bool TryGetView(EntityUid agent, EntityUid? actor, out EntityUid user, out AgentView view)
    {
        user = actor ?? EntityUid.Invalid;
        view = null!;
        if (!user.Valid ||
            Deleted(user) ||
            !_views.TryGetValue((agent, user), out var found) ||
            !IsViewAuthenticated(agent, user, found))
            return false;

        view = found;
        return true;
    }

    private bool TryGetAnyView(EntityUid agent, EntityUid? actor, out EntityUid user, out AgentView view)
    {
        user = actor ?? EntityUid.Invalid;
        view = null!;
        if (!user.Valid || Deleted(user) || !_views.TryGetValue((agent, user), out var found))
            return false;

        view = found;
        return true;
    }

    private bool TryGetOrBindOwner(EntityUid agent, EntityUid user, out NCCharacterId owner)
    {
        if (TryComp<NCAgentIdentityComponent>(agent, out var identity) && identity.CharacterId.IsValid)
        {
            owner = identity.CharacterId;
            // Opening a stolen or recovered Agent makes that physical device the active endpoint
            // without changing its original character ownership.
            _boundAgents[owner] = agent;
            return true;
        }

        if (!TryGetCharacterId(user, out owner))
            return false;

        BindAgent(agent, owner);
        return true;
    }

    private bool TryGetCharacterId(EntityUid user, out NCCharacterId characterId)
    {
        characterId = default;
        return _players.TryGetSessionByEntity(user, out var session) &&
               _preferences.TryGetSelectedNCCharacterId(session.UserId, out characterId);
    }

    private void BindAgent(EntityUid agent, NCCharacterId owner)
    {
        var identity = EnsureComp<NCAgentIdentityComponent>(agent);
        identity.CharacterId = owner;
        _boundAgents[owner] = agent;
    }

    private bool TryFindEquippedAgent(EntityUid user, NCCharacterId owner, out EntityUid agent)
    {
        if (_inventory.TryGetSlotEntity(user, "id", out var id) &&
            id is { } idEntity && IsAgentOwnedByOrUnbound(idEntity, owner))
        {
            agent = idEntity;
            return true;
        }

        if (_inventory.TryGetSlotEntity(user, "belt", out var belt) &&
            belt is { } beltEntity && IsAgentOwnedByOrUnbound(beltEntity, owner))
        {
            agent = beltEntity;
            return true;
        }

        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (!IsAgentOwnedByOrUnbound(held, owner))
                continue;

            agent = held;
            return true;
        }

        agent = EntityUid.Invalid;
        return false;
    }

    /// <summary>
    /// Finds the physical Agent currently usable by a holder without assuming that its stored identity
    /// belongs to that holder. This preserves vanilla stolen-PDA behavior for Agent services.
    /// </summary>
    private bool TryFindUsableAgent(EntityUid user, out EntityUid agent)
    {
        if (_inventory.TryGetSlotEntity(user, "id", out var id) &&
            id is { } idEntity && HasComp<NCAgentComponent>(idEntity))
        {
            agent = idEntity;
            return true;
        }

        if (_inventory.TryGetSlotEntity(user, "belt", out var belt) &&
            belt is { } beltEntity && HasComp<NCAgentComponent>(beltEntity))
        {
            agent = beltEntity;
            return true;
        }

        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (!HasComp<NCAgentComponent>(held))
                continue;

            agent = held;
            return true;
        }

        agent = EntityUid.Invalid;
        return false;
    }

    /// <summary>
    /// Uses the spatial lookup only while the Contacts app is refreshed; no world-wide player scan is performed.
    /// Every returned character is carrying an Agent in a usable slot or hand within the configured range.
    /// </summary>
    private List<NCCharacterId> FindNearbyAgentOwners(EntityUid user, NCCharacterId owner)
    {
        var owners = new HashSet<NCCharacterId>();
        foreach (var candidate in _lookup.GetEntitiesInRange(
                     user,
                     Configuration.ContactShareRange,
                     LookupFlags.Dynamic | LookupFlags.Uncontained))
        {
            if (candidate == user ||
                !TryFindUsableAgent(candidate, out var candidateAgent) ||
                !TryGetOrBindOwner(candidateAgent, candidate, out var candidateOwner) ||
                candidateOwner == owner)
            {
                continue;
            }

            owners.Add(candidateOwner);
        }

        return owners.ToList();
    }

    /// <summary>
    /// Repeats the proximity and physical-Agent validation when the client selects an exchange target.
    /// </summary>
    private bool TryFindNearbyAgentHolder(EntityUid user, NCCharacterId target, out EntityUid holder)
    {
        foreach (var candidate in _lookup.GetEntitiesInRange(
                     user,
                     Configuration.ContactShareRange,
                     LookupFlags.Dynamic | LookupFlags.Uncontained))
        {
            if (candidate == user ||
                !TryFindUsableAgent(candidate, out var candidateAgent) ||
                !TryGetOrBindOwner(candidateAgent, candidate, out var candidateOwner) ||
                candidateOwner != target)
            {
                continue;
            }

            holder = candidate;
            return true;
        }

        holder = EntityUid.Invalid;
        return false;
    }

    private bool IsAgentOwnedByOrUnbound(EntityUid agent, NCCharacterId owner)
    {
        if (!HasComp<NCAgentComponent>(agent))
            return false;

        return !TryComp<NCAgentIdentityComponent>(agent, out var identity) ||
               !identity.CharacterId.IsValid ||
               identity.CharacterId == owner;
    }

    /// <summary>
    /// Resolves an online Agent on demand instead of relying solely on a spawn-time cache.
    /// This is event-driven and scans player sessions only for an explicit delivery attempt.
    /// </summary>
    private bool TryResolveOnlineAgent(NCCharacterId owner, out EntityUid agent, out EntityUid holder)
    {
        if (_boundAgents.TryGetValue(owner, out agent) &&
            TryGetAgentHolder(agent, out holder) &&
            _players.TryGetSessionByEntity(holder, out _))
            return true;

        foreach (var session in _players.Sessions)
        {
            if (!_preferences.TryGetSelectedNCCharacterId(session.UserId, out var activeCharacter) ||
                activeCharacter != owner ||
                session.AttachedEntity is not { Valid: true } user ||
                !TryFindEquippedAgent(user, owner, out agent))
            {
                continue;
            }

            BindAgent(agent, owner);
            holder = user;
            return true;
        }

        agent = EntityUid.Invalid;
        holder = EntityUid.Invalid;
        return false;
    }

    private void HandleDatabaseError(EntityUid agent, EntityUid user, string operation, Exception exception)
    {
        Log.Error($"Agent {agent} failed to {operation}: {exception}");
        if (_views.TryGetValue((agent, user), out var view))
            view.Notice = "DATABASE LINK FAILURE";
    }

    private void OnAgentShutdown(Entity<NCAgentComponent> ent, ref ComponentShutdown args)
    {
        foreach (var key in _views.Keys.Where(value => value.Agent == ent.Owner).ToArray())
        {
            _views.Remove(key);
            _processing.Remove(key);
        }

        ClearAgentUnlockSessions(ent.Owner);

        if (TryComp<NCAgentIdentityComponent>(ent, out var identity) &&
            _boundAgents.GetValueOrDefault(identity.CharacterId) == ent.Owner)
            _boundAgents.Remove(identity.CharacterId);

        EndCallForAgent(ent.Owner);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _views.Clear();
        _processing.Clear();
        _boundAgents.Clear();
        ClearAgentLocks();
        ClearCommunication();
        ClearCalls();
    }

    private sealed class AgentView(NCCharacterId owner)
    {
        public readonly NCCharacterId Owner = owner;
        public readonly HashSet<int> ContactIds = [];
        public readonly HashSet<int> ConversationIds = [];
        public readonly HashSet<int> VisibleDirectoryIds = [];
        public readonly HashSet<int> NearbyShareIds = [];
        public readonly HashSet<int> BlockedIds = [];
        public readonly HashSet<long> GroupIds = [];
        public readonly Dictionary<long, string> GroupNames = [];
        public readonly HashSet<int> GroupMemberIds = [];
        public readonly HashSet<long> PostIds = [];
        public readonly Dictionary<NCCharacterId, (string Alias, string Address)> Targets = [];
        public NCAgentApp ActiveApp = NCAgentApp.Home;
        public NCCharacterId? SelectedCharacterId;
        public long? SelectedGroupId;
        public int FeedOffset;
        public string DirectoryQuery = string.Empty;
        public string Notice = string.Empty;
        public string OwnAlias = string.Empty;
        public string OwnAddress = string.Empty;
        public bool Authenticated;
    }
}
