// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.CartridgeLoader;
using Content.Client._NC.Agent.Shards;
using Content.Client.PDA;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Agent.TraumaTeam;
using Content.Shared._NC.Missions.UI;
using Content.Shared._NC.Agent.Shards;
using Content.Shared.CartridgeLoader;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.PDA;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Content.Client._NC.Meta.UI;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Agent;

/// <summary>
/// Hosts the existing PDA and cartridge behavior inside the Night City Agent interface.
/// </summary>
[UsedImplicitly]
public sealed class NCAgentBoundUserInterface : CartridgeLoaderBoundUserInterface
{
    private readonly PdaSystem _pdaSystem;
    private readonly NCAgentShardSystem _shardSystem;

    [ViewVariables]
    private NCAgentMenu? _menu;
    private readonly NCMetaApplicationSessionManager _metaApplications;

    public NCAgentBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _pdaSystem = EntMan.System<PdaSystem>();
        _shardSystem = EntMan.System<NCAgentShardSystem>();
        _metaApplications = new(view => _menu is { Disposed: false } && _menu.TryShowMetaWebsite(view));
        _metaApplications.Requested += SendMessage;
        _metaApplications.WebsiteNotice += notice => _menu?.ShowMetaSiteNotice(notice);
    }

    protected override void Open()
    {
        base.Open();

        if (_menu is null or { Disposed: true })
            CreateMenu();
    }

    private void CreateMenu()
    {
        _menu = this.CreateWindowCenteredLeft<NCAgentMenu>();

        _menu.FlashlightButton.OnToggled += _ => SendMessage(new PdaToggleFlashlightMessage());
        _menu.EjectVinButton.OnPressed += _ =>
            SendPredictedMessage(new ItemSlotButtonPressedEvent(PdaComponent.PdaIdSlotId));
        _menu.EjectPenButton.OnPressed += _ =>
            SendPredictedMessage(new ItemSlotButtonPressedEvent(PdaComponent.PdaPenSlotId));
        _menu.EjectPaiButton.OnPressed += _ =>
            SendPredictedMessage(new ItemSlotButtonPressedEvent(PdaComponent.PdaPaiSlotId));
        _menu.MusicButton.OnPressed += _ => SendMessage(new PdaShowMusicMessage());
        _menu.RingtoneButton.OnPressed += _ => SendMessage(new PdaShowRingtoneMessage());
        _menu.ShowUplinkButton.OnPressed += _ => SendMessage(new PdaShowUplinkMessage());
        _menu.LockUplinkButton.OnPressed += _ => SendMessage(new PdaLockUplinkMessage());

        _menu.OnProgramItemPressed += ActivateCartridge;
        _menu.OnInstallButtonPressed += InstallCartridge;
        _menu.OnUninstallButtonPressed += UninstallCartridge;
        _menu.OnEjectShard += () =>
            SendPredictedMessage(new ItemSlotButtonPressedEvent(CartridgeLoaderComponent.CartridgeSlotId));
        _menu.ProgramCloseButton.OnPressed += _ => DeactivateActiveCartridge();
        _menu.OnOpenAgentApp += app => SendMessage(new NCAgentOpenAppMessage(app));
        _menu.MetaSiteRequested += address =>
        {
            _metaApplications.CloseWebsite();
            SendMessage(new NCMetaBrowserNavigateMessage(Guid.NewGuid(), address));
        };
        _menu.OnFixerWorkRequest += SendMessage;
        _menu.OnContractRequest += SendMessage;
        _menu.OnTraumaTeamRequest += SendMessage;
        _menu.OnDirectorySearch += query => SendMessage(new NCAgentDirectorySearchMessage(query));
        _menu.OnSetAlias += alias => SendMessage(new NCAgentSetAliasMessage(alias));
        _menu.OnSetPresence += presence => SendMessage(new NCAgentSetPresenceMessage(presence));
        _menu.OnSetNotificationSettings += (overlay, sound) =>
            SendMessage(new NCAgentSetNotificationSettingsMessage(overlay, sound));
        _menu.OnAddContact += characterId => SendMessage(new NCAgentAddContactMessage(characterId));
        _menu.OnRemoveContact += characterId => SendMessage(new NCAgentRemoveContactMessage(characterId));
        _menu.OnRequestContactExchange += characterId =>
            SendMessage(new NCAgentRequestContactExchangeMessage(characterId));
        _menu.OnSetBlocked += (characterId, blocked) =>
            SendMessage(new NCAgentSetBlockedMessage(characterId, blocked));
        _menu.OnClearConversation += characterId =>
            SendMessage(new NCAgentClearConversationMessage(characterId));
        _menu.OnSelectConversation += characterId => SendMessage(new NCAgentSelectConversationMessage(characterId));
        _menu.OnSendDirectMessage += (recipient, body) =>
            SendMessage(new NCAgentSendMessageMessage(recipient, body));
        _menu.OnCreateGroup += (name, members) => SendMessage(new NCAgentCreateGroupMessage(name, members));
        _menu.OnSelectGroup += groupId => SendMessage(new NCAgentSelectGroupMessage(groupId));
        _menu.OnSendGroupMessage += (groupId, body) => SendMessage(new NCAgentSendGroupMessage(groupId, body));
        _menu.OnInviteGroupMember += (groupId, characterId) =>
            SendMessage(new NCAgentInviteGroupMemberMessage(groupId, characterId));
        _menu.OnRespondGroupInvite += (groupId, accept) =>
            SendMessage(new NCAgentRespondGroupInviteMessage(groupId, accept));
        _menu.OnRemoveGroupMember += (groupId, characterId) =>
            SendMessage(new NCAgentRemoveGroupMemberMessage(groupId, characterId));
        _menu.OnLeaveGroup += groupId => SendMessage(new NCAgentLeaveGroupMessage(groupId));
        _menu.OnClearGroupConversation += groupId =>
            SendMessage(new NCAgentClearGroupConversationMessage(groupId));
        _menu.OnCreatePost += body => SendMessage(new NCAgentCreatePostMessage(body));
        _menu.OnCreateComment += (postId, body) => SendMessage(new NCAgentCreateCommentMessage(postId, body));
        _menu.OnToggleReaction += postId => SendMessage(new NCAgentToggleReactionMessage(postId));
        _menu.OnDeletePost += postId => SendMessage(new NCAgentDeletePostMessage(postId));
        _menu.OnSetFeedOffset += offset => SendMessage(new NCAgentSetFeedOffsetMessage(offset));
        _menu.OnStartCall += target => SendMessage(new NCAgentStartCallMessage(target));
        _menu.OnAnswerCall += accept => SendMessage(new NCAgentAnswerCallMessage(accept));
        _menu.OnEndCall += () => SendMessage(new NCAgentEndCallMessage());
        _menu.OnUnlockPin += pin => SendMessage(new NCAgentUnlockMessage(pin));
        _menu.OnSetPin += (currentPin, newPin) => SendMessage(new NCAgentSetPinMessage(currentPin, newPin));
        _menu.OnDisablePin += currentPin => SendMessage(new NCAgentDisablePinMessage(currentPin));
        _menu.OnLockAgent += () => SendMessage(new NCAgentLockMessage());
        _menu.OnCityAction += (action, id, version) =>
            SendMessage(new NCAgentCityActionMessage(action, id, version));

    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (_menu is { Disposed: true })
            return;

        base.UpdateState(state);

        if (state is not PdaUpdateState updateState)
            return;

        if (_menu == null)
        {
            _pdaSystem.Log.Error("Agent state received before its menu was created.");
            return;
        }

        _menu.UpdateState(updateState);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (_menu is { Disposed: true })
            return;

        base.ReceiveMessage(message);
        if (_menu == null)
            return;

        switch (message)
        {
            case NCMetaAppUpdateMessage app:
                _metaApplications.TryApplyState(app.State);
                break;
            case NCMetaAppActionResultMessage action:
                _metaApplications.ApplyResult(action);
                break;
            case NCMetaBrowserResultMessage result:
                _menu.ShowMetaSiteNotice(result.Notice);
                break;
            case NCAgentContractsUpdateMessage contracts:
                _menu.UpdateContractData(contracts);
                break;
            case NCAgentTraumaTeamUpdateMessage traumaTeam:
                _menu.UpdateTraumaTeamData(traumaTeam);
                break;
            case NCFixerMissionUpdateMessage missions:
                _menu.UpdateMissionData(missions);
                break;
            case NCAgentLockUpdateMessage lockUpdate:
                _menu.UpdateLockState(lockUpdate);
                break;
            case NCAgentUpdateMessage update:
                _menu.UpdateAgentData(update);
                break;
            case NCAgentMapUpdateMessage mapUpdate:
                if (EntMan.TryGetComponent<TransformComponent>(Owner, out var transform))
                    _menu.SetMapSource(transform.GridUid);
                _menu.UpdateMapData(mapUpdate);
                break;
            case NCAgentMapTrackingUpdateMessage trackingUpdate:
                if (EntMan.TryGetComponent<TransformComponent>(Owner, out var trackingTransform))
                    _menu.SetMapSource(trackingTransform.GridUid);
                _menu.UpdateMapTrackingData(trackingUpdate);
                break;
            case NCAgentCityUpdateMessage cityUpdate:
                _menu.UpdateCityData(cityUpdate);
                break;
        }
    }

    protected override void AttachCartridgeUI(Control cartridgeUiFragment, string? title)
    {
        if (_menu is not { Disposed: false })
            return;

        _menu?.ProgramView.AddChild(cartridgeUiFragment);
        _menu?.ToProgramView(title ?? Loc.GetString("comp-pda-io-program-fallback-title"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _metaApplications.Dispose();
        base.Dispose(disposing);
    }

    protected override void DetachCartridgeUI(Control cartridgeUiFragment)
    {
        if (_menu is not { Disposed: false })
            return;

        _menu.ToHomeScreen();
        _menu.HideProgramHeader();
        _menu.ProgramView.RemoveChild(cartridgeUiFragment);
    }

    protected override void UpdateAvailablePrograms(List<(EntityUid, CartridgeComponent)> programs)
    {
        if (_menu is not { Disposed: false })
            return;

        var storage = EntMan.GetComponentOrNull<NCAgentShardStorageComponent>(Owner);
        var defaultSize = Math.Max(1, storage?.DefaultProgramSize ?? 1);
        var capacity = Math.Max(1, storage?.Capacity ?? 8);
        var displayPrograms = new List<NCAgentShardDisplayData>(programs.Count);

        foreach (var (uid, cartridge) in programs)
        {
            var shard = EntMan.GetComponentOrNull<NCAgentShardComponent>(uid);
            var meta = EntMan.GetComponentOrNull<MetaDataComponent>(uid);
            displayPrograms.Add(new NCAgentShardDisplayData(
                uid,
                shard?.DisplayName ?? Loc.GetString(cartridge.ProgramName),
                shard?.Description ?? meta?.EntityDescription ?? "Legacy cartridge program.",
                shard is null ? defaultSize : Math.Max(1, shard.Size),
                shard?.Category ?? NCAgentShardCategory.Legacy,
                shard?.Permissions ?? [],
                shard?.RequiredAccess ?? [],
                shard?.ServerAuthorized ?? false,
                shard?.AccessLabel ?? "PUBLIC",
                _shardSystem.IsAuthorized(Owner, uid),
                cartridge.InstallationStatus,
                cartridge.Icon));
        }

        _menu.UpdateAvailableShards(displayPrograms, _shardSystem.GetUsedMemory(Owner), capacity);
    }
}
