// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.Agent.Components;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Chat;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Robust.Shared.Utility;
using Robust.Shared.Timing;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private NCConversationSessionSystem _conversationSessions = default!;

    private readonly Dictionary<NCCharacterId, AgentCall> _calls = [];
    private TimeSpan _nextCallTimeoutCheck;

    private void InitializeCalls()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentStartCallMessage>(OnStartCall);
        SubscribeLocalEvent<NCAgentComponent, NCAgentAnswerCallMessage>(OnAnswerCall);
        SubscribeLocalEvent<NCAgentComponent, NCAgentEndCallMessage>(OnEndCall);
        SubscribeLocalEvent<NCAgentComponent, EntParentChangedMessage>(OnAgentParentChanged);
        SubscribeLocalEvent<NCActiveAgentCallComponent, EntitySpokeEvent>(OnSpokeDuringCall);
    }

    private async void OnStartCall(EntityUid uid, NCAgentComponent component, NCAgentStartCallMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.ContactIds.Contains(args.TargetCharacterId) ||
            !IsAgentUsableBy(user, uid) ||
            _calls.ContainsKey(view.Owner))
            return;

        var target = new NCCharacterId(args.TargetCharacterId);
        if (!view.Targets.TryGetValue(target, out var targetDetails))
            return;

        // NC: Serialize an individual Agent's dial attempt across the asynchronous DB policy checks.
        if (!_processing.Add((uid, user)))
            return;

        try
        {
            var startedAt = DateTime.UtcNow;
            if (await _database.AreNCAgentProfilesBlockedAsync(view.Owner, target))
            {
                view.Notice = "ADDRESS UNAVAILABLE";
                await SendViewAsync(uid, user);
                return;
            }

            var targetProfile = await _database.GetOrCreateNCAgentProfileAsync(target, Configuration.AddressPrefix);
            if (targetProfile == null ||
                targetProfile.Presence is NCAgentPresenceStatus.DoNotDisturb or NCAgentPresenceStatus.Offline)
            {
                view.Notice = "ADDRESS UNAVAILABLE";
                await SendViewAsync(uid, user);
                return;
            }

            if (_calls.ContainsKey(view.Owner) || _calls.ContainsKey(target) ||
                !TryResolveOnlineAgent(target, out var targetAgent, out var targetHolder))
            {
                view.Notice = "TARGET OFFLINE OR BUSY";
                _ = PersistCallLogAsync(
                    view.Owner,
                    target,
                    startedAt,
                    null,
                    startedAt,
                    NCAgentCallOutcome.Busy);
                _ = SendViewAsync(uid, user);
                return;
            }

            if (!CanConnectCall(user, targetHolder, out var networkFailure))
            {
                view.Notice = networkFailure;
                await SendViewAsync(uid, user);
                return;
            }

            var call = new AgentCall(
                view.Owner,
                target,
                uid,
                targetAgent,
                view.OwnAlias,
                view.OwnAddress,
                targetDetails.Alias,
                targetDetails.Address,
                startedAt,
                _gameTiming.CurTime + Configuration.IncomingCallTimeout)
            {
                CallerHolder = user,
                TargetHolder = targetHolder,
            };

            _calls[view.Owner] = call;
            _calls[target] = call;
            view.Notice = targetProfile.Presence == NCAgentPresenceStatus.Busy
                ? "CALLING // TARGET BUSY"
                : "CALLING";

            await SendAgentNotificationAsync(
                target,
                NCAgentNotificationKind.IncomingCall,
                "INCOMING CALL",
                $"Incoming call from {call.CallerName}",
                false,
                peer: view.Owner,
                app: NCAgentApp.Calls,
                knownHolder: targetHolder);
            _ = RefreshCallViewsAsync(call);
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "start call", exception);
            await SendViewAsync(uid, user);
        }
        finally
        {
            FinishOperation(uid, user);
        }
    }

    private void OnAnswerCall(Entity<NCAgentComponent> ent, ref NCAgentAnswerCallMessage args)
    {
        if (!TryGetView(ent.Owner, args.Actor, out var user, out var view) ||
            !_calls.TryGetValue(view.Owner, out var call) ||
            call.Target != view.Owner || call.Connected ||
            call.TargetAgent != ent.Owner || !IsAgentUsableBy(user, ent.Owner))
            return;

        if (!args.Accept)
        {
            EndCall(call, "CALL REJECTED", NCAgentCallOutcome.Rejected);
            return;
        }

        if (!TryGetAgentHolder(call.CallerAgent, out var callerHolder))
        {
            EndCall(call, "CALLER LOST SIGNAL", NCAgentCallOutcome.SignalLost);
            return;
        }

        call.Connected = true;
        call.AnsweredAt = DateTime.UtcNow;
        call.CallerHolder = callerHolder;
        call.TargetHolder = user;
        var conversationId = NCConversationSessionId.New();
        if (_conversationSessions.TryStart(new NCConversationStartRequest(
                conversationId,
                [NCPartyRef.ForCharacter(call.Caller), NCPartyRef.ForCharacter(call.Target)],
                NCConversationChannel.AgentCall,
                new NCInformationSubject(NCInformationSubjectKind.StableKey, "agent-network"),
                ["agent-call"],
                [],
                NCMemoryPrivacy.Restricted,
                NCLanguage.Russian,
                NCLocale.RuRu), out _, out _))
        {
            call.ConversationSessionId = conversationId;
            _conversationSessions.TryTransition(conversationId, NCConversationStatus.Active, string.Empty, out _);
        }
        AttachCallRelay(callerHolder, call.Caller, call.CallerAgent);
        AttachCallRelay(user, call.Target, call.TargetAgent);
        NotifyCallHolder(callerHolder, $"Connected to {call.TargetName}.");
        NotifyCallHolder(user, $"Connected to {call.CallerName}.");
        _ = RefreshCallViewsAsync(call);
    }

    private void OnEndCall(Entity<NCAgentComponent> ent, ref NCAgentEndCallMessage args)
    {
        if (!TryGetView(ent.Owner, args.Actor, out var user, out var view) ||
            !_calls.TryGetValue(view.Owner, out var call) ||
            (call.CallerAgent != ent.Owner && call.TargetAgent != ent.Owner) ||
            !IsAgentUsableBy(user, ent.Owner))
            return;

        EndCall(
            call,
            "CALL ENDED",
            call.Connected ? NCAgentCallOutcome.Completed : NCAgentCallOutcome.Cancelled);
    }

    private void OnAgentParentChanged(Entity<NCAgentComponent> ent, ref EntParentChangedMessage args)
    {
        if (!TryComp<NCAgentIdentityComponent>(ent, out var identity) ||
            !_calls.TryGetValue(identity.CharacterId, out var call))
            return;

        if (args.OldParent is { } oldParent &&
            TryComp<NCActiveAgentCallComponent>(oldParent, out var oldRelay) && oldRelay.Agent == ent.Owner)
            RemComp<NCActiveAgentCallComponent>(oldParent);

        var newParent = args.Transform.ParentUid;
        if (!newParent.Valid || !IsAgentUsableBy(newParent, ent.Owner))
        {
            EndCall(call, "SIGNAL LOST", NCAgentCallOutcome.SignalLost);
            return;
        }

        SetCallHolder(call, identity.CharacterId, newParent);
        AttachCallRelay(newParent, identity.CharacterId, ent.Owner);
    }

    private void OnSpokeDuringCall(Entity<NCActiveAgentCallComponent> ent, ref EntitySpokeEvent args)
    {
        if (args.Channel != null || args.ObfuscatedMessage != null ||
            !_calls.TryGetValue(ent.Comp.DeviceOwner, out var call) || !call.Connected ||
            !IsAgentUsableBy(ent.Owner, ent.Comp.Agent))
            return;

        var speakerIsCaller = call.Caller == ent.Comp.DeviceOwner;
        var remoteAgent = speakerIsCaller ? call.TargetAgent : call.CallerAgent;
        var speakerName = speakerIsCaller ? call.CallerName : call.TargetName;
        if (!TryGetAgentHolder(remoteAgent, out var remoteHolder) ||
            !_players.TryGetSessionByEntity(remoteHolder, out var remoteSession))
            return;

        var escapedName = FormattedMessage.EscapeText(speakerName);
        var escapedMessage = FormattedMessage.EscapeText(args.Message);
        var wrapped = $"[color=#69F6E7][bold]AGENT // {escapedName}[/bold][/color] {escapedMessage}";
        _chat.ChatMessageToOne(
            ChatChannel.Radio,
            args.Message,
            wrapped,
            args.Source,
            false,
            remoteSession.Channel,
            colorOverride: Color.FromHex("#69F6E7"));
    }

    private NCAgentCallSummary BuildCallSummary(NCCharacterId owner)
    {
        if (!_calls.TryGetValue(owner, out var call))
            return new NCAgentCallSummary(NCAgentCallState.None, null, string.Empty, string.Empty);

        var isCaller = call.Caller == owner;
        var state = call.Connected
            ? NCAgentCallState.Connected
            : isCaller
                ? NCAgentCallState.Outgoing
                : NCAgentCallState.Incoming;
        return new NCAgentCallSummary(
            state,
            (isCaller ? call.Target : call.Caller).Value,
            isCaller ? call.TargetName : call.CallerName,
            isCaller ? call.TargetAddress : call.CallerAddress);
    }

    /// <summary>
    /// Keeps an in-flight call consistent when either participant changes their CitiNet handle.
    /// </summary>
    private void UpdateActiveCallAlias(NCCharacterId owner, string alias)
    {
        if (!_calls.TryGetValue(owner, out var call))
            return;

        if (call.Caller == owner)
            call.CallerName = alias;
        else if (call.Target == owner)
            call.TargetName = alias;
    }

    private bool TryGetAgentHolder(EntityUid agent, out EntityUid holder)
    {
        holder = EntityUid.Invalid;
        if (Deleted(agent))
            return false;

        var parent = Transform(agent).ParentUid;
        if (!parent.Valid || !IsAgentUsableBy(parent, agent))
            return false;

        holder = parent;
        return true;
    }

    private bool IsAgentUsableBy(EntityUid user, EntityUid agent)
    {
        if (Deleted(user) || Deleted(agent))
            return false;

        if (_hands.IsHolding(user, agent))
            return true;

        return _inventory.TryGetSlotEntity(user, "id", out var id) && id == agent ||
               _inventory.TryGetSlotEntity(user, "belt", out var belt) && belt == agent;
    }

    private void AttachCallRelay(EntityUid holder, NCCharacterId deviceOwner, EntityUid agent)
    {
        var relay = EnsureComp<NCActiveAgentCallComponent>(holder);
        relay.DeviceOwner = deviceOwner;
        relay.Agent = agent;
    }

    private void SetCallHolder(AgentCall call, NCCharacterId owner, EntityUid? holder)
    {
        if (call.Caller == owner)
            call.CallerHolder = holder;
        else if (call.Target == owner)
            call.TargetHolder = holder;
    }

    private void NotifyCallHolder(EntityUid holder, string text)
    {
        if (_players.TryGetSessionByEntity(holder, out var session))
            _chat.DispatchServerMessage(session, $"[AGENT] {text}");
    }

    private void EndCallForAgent(EntityUid agent)
    {
        var call = _calls.Values.FirstOrDefault(value => value.CallerAgent == agent || value.TargetAgent == agent);
        if (call != null)
            EndCall(call, "SIGNAL LOST", NCAgentCallOutcome.SignalLost);
    }

    private void EndCall(
        AgentCall call,
        string reason,
        NCAgentCallOutcome outcome,
        string? targetReason = null)
    {
        _calls.Remove(call.Caller);
        _calls.Remove(call.Target);
        if (call.ConversationSessionId is { IsValid: true } conversationId)
        {
            var status = outcome == NCAgentCallOutcome.Completed
                ? NCConversationStatus.Ended
                : NCConversationStatus.Interrupted;
            _conversationSessions.TryTransition(
                conversationId,
                status,
                outcome.ToString().ToLowerInvariant(),
                out _);
        }
        RemoveCallRelay(call.CallerHolder, call.CallerAgent);
        RemoveCallRelay(call.TargetHolder, call.TargetAgent);

        if (call.CallerHolder is { } callerHolder && !Deleted(callerHolder))
            NotifyCallHolder(callerHolder, reason);
        if (call.TargetHolder is { } targetHolder && !Deleted(targetHolder))
            NotifyCallHolder(targetHolder, targetReason ?? reason);

        _ = PersistCallLogAsync(
            call.Caller,
            call.Target,
            call.StartedAt,
            call.AnsweredAt,
            DateTime.UtcNow,
            outcome);

        if (outcome == NCAgentCallOutcome.Missed)
        {
            _ = SendAgentNotificationAsync(
                call.Target,
                NCAgentNotificationKind.MissedCall,
                "MISSED CALL",
                $"Missed call from {call.CallerName}",
                false,
                peer: call.Caller,
                app: NCAgentApp.Calls);
        }
    }

    private void RemoveCallRelay(EntityUid? holder, EntityUid agent)
    {
        if (holder is not { } uid || Deleted(uid) ||
            !TryComp<NCActiveAgentCallComponent>(uid, out var relay) || relay.Agent != agent)
            return;

        RemComp<NCActiveAgentCallComponent>(uid);
    }

    private async Task RefreshCallViewsAsync(AgentCall call)
    {
        await RefreshOwnerViewsAsync(call.Caller);
        await RefreshOwnerViewsAsync(call.Target);
    }

    private void UpdateCallTimeouts()
    {
        var now = _gameTiming.CurTime;
        if (_calls.Count == 0 || now < _nextCallTimeoutCheck)
            return;

        // NC: Calls are indexed twice, so inspect only caller keys and throttle the scan to once per second.
        _nextCallTimeoutCheck = now + TimeSpan.FromSeconds(1);
        while (true)
        {
            AgentCall? expired = null;
            foreach (var (owner, call) in _calls)
            {
                if (owner == call.Caller && !call.Connected && now >= call.Deadline)
                {
                    expired = call;
                    break;
                }
            }

            if (expired == null)
                return;

            EndCall(expired, "NO ANSWER", NCAgentCallOutcome.Missed, "MISSED CALL");
        }
    }

    private async Task PersistCallLogAsync(
        NCCharacterId caller,
        NCCharacterId target,
        DateTime startedAt,
        DateTime? answeredAt,
        DateTime endedAt,
        NCAgentCallOutcome outcome)
    {
        try
        {
            await _database.CreateNCAgentCallLogAsync(
                caller,
                target,
                startedAt,
                answeredAt,
                endedAt,
                outcome);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist Agent call history: {exception}");
        }
        finally
        {
            await RefreshOwnerViewsAsync(caller);
            await RefreshOwnerViewsAsync(target);
        }
    }

    private void ClearCalls()
    {
        foreach (var call in _calls.Values.Distinct().ToArray())
            EndCall(call, "SIGNAL LOST", NCAgentCallOutcome.SignalLost);
    }

    private sealed class AgentCall(
        NCCharacterId caller,
        NCCharacterId target,
        EntityUid callerAgent,
        EntityUid targetAgent,
        string callerName,
        string callerAddress,
        string targetName,
        string targetAddress,
        DateTime startedAt,
        TimeSpan deadline)
    {
        public readonly NCCharacterId Caller = caller;
        public readonly NCCharacterId Target = target;
        public readonly EntityUid CallerAgent = callerAgent;
        public readonly EntityUid TargetAgent = targetAgent;
        public string CallerName = callerName;
        public readonly string CallerAddress = callerAddress;
        public string TargetName = targetName;
        public readonly string TargetAddress = targetAddress;
        public readonly DateTime StartedAt = startedAt;
        public readonly TimeSpan Deadline = deadline;
        public EntityUid? CallerHolder;
        public NCConversationSessionId? ConversationSessionId;
        public EntityUid? TargetHolder;
        public bool Connected;
        public DateTime? AnsweredAt;
    }
}
