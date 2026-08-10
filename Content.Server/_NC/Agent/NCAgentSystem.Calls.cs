// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.Agent.Components;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Content.Shared.Chat;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Robust.Shared.Utility;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    [Dependency] private SharedHandsSystem _hands = default!;

    private readonly Dictionary<NCCharacterId, AgentCall> _calls = [];

    private void InitializeCalls()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentStartCallMessage>(OnStartCall);
        SubscribeLocalEvent<NCAgentComponent, NCAgentAnswerCallMessage>(OnAnswerCall);
        SubscribeLocalEvent<NCAgentComponent, NCAgentEndCallMessage>(OnEndCall);
        SubscribeLocalEvent<NCAgentComponent, EntParentChangedMessage>(OnAgentParentChanged);
        SubscribeLocalEvent<NCActiveAgentCallComponent, EntitySpokeEvent>(OnSpokeDuringCall);
    }

    private void OnStartCall(Entity<NCAgentComponent> ent, ref NCAgentStartCallMessage args)
    {
        if (!TryGetView(ent.Owner, args.Actor, out var user, out var view) ||
            !view.ContactIds.Contains(args.TargetCharacterId) ||
            !IsAgentUsableBy(user, ent.Owner) ||
            _calls.ContainsKey(view.Owner))
            return;

        var target = new NCCharacterId(args.TargetCharacterId);
        if (_calls.ContainsKey(target) ||
            !TryResolveOnlineAgent(target, out var targetAgent, out var targetHolder) ||
            !view.Targets.TryGetValue(target, out var targetDetails))
        {
            view.Notice = "TARGET OFFLINE OR BUSY";
            _ = SendViewAsync(ent.Owner, user);
            return;
        }

        var call = new AgentCall(
            view.Owner,
            target,
            ent.Owner,
            targetAgent,
            view.OwnAlias,
            view.OwnAddress,
            targetDetails.Alias,
            targetDetails.Address)
        {
            CallerHolder = user,
            TargetHolder = targetHolder,
        };

        _calls[view.Owner] = call;
        _calls[target] = call;
        view.Notice = "CALLING";

        _ringer.RingerPlayRingtone(targetAgent);
        NotifyCallHolder(targetHolder, $"Incoming Agent call from {call.CallerName}. Open CALLS to answer.");
        _ = RefreshCallViewsAsync(call);
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
            EndCall(call, "CALL REJECTED");
            return;
        }

        if (!TryGetAgentHolder(call.CallerAgent, out var callerHolder))
        {
            EndCall(call, "CALLER LOST SIGNAL");
            return;
        }

        call.Connected = true;
        call.CallerHolder = callerHolder;
        call.TargetHolder = user;
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

        EndCall(call, "CALL ENDED");
    }

    private void OnAgentParentChanged(Entity<NCAgentComponent> ent, ref EntParentChangedMessage args)
    {
        if (!TryComp<NCAgentIdentityComponent>(ent, out var identity) ||
            !_calls.TryGetValue(identity.CharacterId, out var call) || !call.Connected)
            return;

        if (args.OldParent is { } oldParent &&
            TryComp<NCActiveAgentCallComponent>(oldParent, out var oldRelay) && oldRelay.Agent == ent.Owner)
            RemComp<NCActiveAgentCallComponent>(oldParent);

        var newParent = args.Transform.ParentUid;
        if (!newParent.Valid || !IsAgentUsableBy(newParent, ent.Owner))
        {
            SetCallHolder(call, identity.CharacterId, null);
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
            EndCall(call, "SIGNAL LOST");
    }

    private void EndCall(AgentCall call, string reason)
    {
        _calls.Remove(call.Caller);
        _calls.Remove(call.Target);
        RemoveCallRelay(call.CallerHolder, call.CallerAgent);
        RemoveCallRelay(call.TargetHolder, call.TargetAgent);

        if (call.CallerHolder is { } callerHolder && !Deleted(callerHolder))
            NotifyCallHolder(callerHolder, reason);
        if (call.TargetHolder is { } targetHolder && !Deleted(targetHolder))
            NotifyCallHolder(targetHolder, reason);

        _ = RefreshCallViewsAsync(call);
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

    private void ClearCalls()
    {
        foreach (var call in _calls.Values.Distinct().ToArray())
        {
            RemoveCallRelay(call.CallerHolder, call.CallerAgent);
            RemoveCallRelay(call.TargetHolder, call.TargetAgent);
        }

        _calls.Clear();
    }

    private sealed class AgentCall(
        NCCharacterId caller,
        NCCharacterId target,
        EntityUid callerAgent,
        EntityUid targetAgent,
        string callerName,
        string callerAddress,
        string targetName,
        string targetAddress)
    {
        public readonly NCCharacterId Caller = caller;
        public readonly NCCharacterId Target = target;
        public readonly EntityUid CallerAgent = callerAgent;
        public readonly EntityUid TargetAgent = targetAgent;
        public string CallerName = callerName;
        public readonly string CallerAddress = callerAddress;
        public string TargetName = targetName;
        public readonly string TargetAddress = targetAddress;
        public EntityUid? CallerHolder;
        public EntityUid? TargetHolder;
        public bool Connected;
    }
}
