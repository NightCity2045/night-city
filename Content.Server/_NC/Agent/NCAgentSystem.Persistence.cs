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
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId) ||
            !TryFindEquippedAgent(args.Mob, out var agent))
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

        _views[(uid, user)] = new AgentView(owner);

        try
        {
            var profile = await _database.GetOrCreateNCAgentProfileAsync(owner, Configuration.AddressPrefix);
            if (profile == null)
                return;

            await _database.PurgeExpiredNCAgentMessagesAsync(DateTime.UtcNow);
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
            !view.ContactIds.Contains(args.CharacterId))
            return;

        view.SelectedCharacterId = new NCCharacterId(args.CharacterId);
        view.ActiveApp = NCAgentApp.Messages;
        view.Notice = string.Empty;
        await SendViewAsync(uid, user);
    }

    private async void OnSendMessage(EntityUid uid, NCAgentComponent component, NCAgentSendMessageMessage args)
    {
        var body = args.Body.Trim();
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            view.SelectedCharacterId?.Value != args.RecipientCharacterId ||
            !view.ContactIds.Contains(args.RecipientCharacterId) ||
            body.Length is < 1 || body.Length > Configuration.MaximumMessageLength ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var recipient = new NCCharacterId(args.RecipientCharacterId);
            var message = await _database.CreateNCAgentMessageAsync(
                view.Owner,
                recipient,
                body,
                now,
                now + Configuration.MessageRetention);
            if (message == null)
                return;

            view.Notice = "MESSAGE SENT";
            NotifyAgent(recipient, $"New message from {message.SenderAlias}");
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
            if (await _database.CreateNCCitiNetPostAsync(view.Owner, body, DateTime.UtcNow) != null)
                view.Notice = "POST PUBLISHED";
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
            if (await _database.CreateNCCitiNetCommentAsync(view.Owner, args.PostId, body, DateTime.UtcNow) != null)
                view.Notice = "COMMENT PUBLISHED";
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
        if (!_views.TryGetValue((agent, user), out var view) || Deleted(agent) || Deleted(user))
            return;

        var config = Configuration;
        var snapshot = await _database.GetNCAgentSnapshotAsync(
            view.Owner,
            view.SelectedCharacterId,
            view.DirectoryQuery,
            DateTime.UtcNow,
            config.DirectoryResultLimit,
            config.ConversationMessageLimit,
            config.FeedPostLimit,
            config.FeedCommentLimit);
        if (snapshot == null || !_views.ContainsKey((agent, user)))
            return;

        view.ContactIds.Clear();
        view.VisibleDirectoryIds.Clear();
        view.PostIds.Clear();
        view.Targets.Clear();
        view.OwnAlias = snapshot.Profile.Alias;
        view.OwnAddress = snapshot.Profile.Address;

        foreach (var contact in snapshot.Contacts)
        {
            view.ContactIds.Add(contact.CharacterId.Value);
            view.Targets[contact.CharacterId] = (contact.Alias, contact.Address);
        }

        foreach (var entry in snapshot.Directory)
        {
            view.VisibleDirectoryIds.Add(entry.CharacterId.Value);
            view.Targets[entry.CharacterId] = (entry.Alias, entry.Address);
        }

        foreach (var post in snapshot.Posts)
            view.PostIds.Add(post.PostId);

        if (view.SelectedCharacterId is { } selected && !view.ContactIds.Contains(selected.Value))
            view.SelectedCharacterId = null;

        var call = BuildCallSummary(view.Owner);
        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentUpdateMessage(
            view.ActiveApp,
            snapshot.Profile.CharacterId.Value,
            snapshot.Profile.Alias,
            snapshot.Profile.Address,
            snapshot.Directory.Select(value => new NCAgentDirectoryEntry(
                value.CharacterId.Value,
                value.Alias,
                value.Address,
                value.IsContact)).ToList(),
            snapshot.Contacts.Select(value => new NCAgentContactSummary(
                value.CharacterId.Value,
                value.Alias,
                value.Address)).ToList(),
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
            snapshot.Posts.Select(value => new NCAgentCitiNetPostSummary(
                value.PostId,
                value.AuthorCharacterId.Value,
                value.AuthorAlias,
                value.Body,
                value.CreatedAt,
                value.ReactionCount,
                value.ReactedByViewer,
                value.Comments.Select(comment => new NCAgentCitiNetCommentSummary(
                    comment.CommentId,
                    comment.AuthorCharacterId.Value,
                    comment.AuthorAlias,
                    comment.Body,
                    comment.CreatedAt)).ToList())).ToList(),
            call,
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

    private bool TryFindEquippedAgent(EntityUid user, out EntityUid agent)
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

        agent = EntityUid.Invalid;
        return false;
    }

    private void NotifyAgent(NCCharacterId owner, string text)
    {
        if (!_boundAgents.TryGetValue(owner, out var agent) || Deleted(agent))
            return;

        _ringer.RingerPlayRingtone(agent);
        if (TryGetAgentHolder(agent, out var holder) && _players.TryGetSessionByEntity(holder, out var session))
            _chat.DispatchServerMessage(session, $"[AGENT] {text}");
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
        ClearCalls();
    }

    private sealed class AgentView(NCCharacterId owner)
    {
        public readonly NCCharacterId Owner = owner;
        public readonly HashSet<int> ContactIds = [];
        public readonly HashSet<int> VisibleDirectoryIds = [];
        public readonly HashSet<long> PostIds = [];
        public readonly Dictionary<NCCharacterId, (string Alias, string Address)> Targets = [];
        public NCAgentApp ActiveApp = NCAgentApp.Home;
        public NCCharacterId? SelectedCharacterId;
        public string DirectoryQuery = string.Empty;
        public string Notice = string.Empty;
        public string OwnAlias = string.Empty;
        public string OwnAddress = string.Empty;
    }
}
