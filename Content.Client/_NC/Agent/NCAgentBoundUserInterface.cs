// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.CartridgeLoader;
using Content.Client.PDA;
using Content.Shared._NC.Agent.UI;
using Content.Shared.CartridgeLoader;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.PDA;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent;

/// <summary>
/// Hosts the existing PDA and cartridge behavior inside the Night City Agent interface.
/// </summary>
[UsedImplicitly]
public sealed class NCAgentBoundUserInterface : CartridgeLoaderBoundUserInterface
{
    private readonly PdaSystem _pdaSystem;

    [ViewVariables]
    private NCAgentMenu? _menu;

    public NCAgentBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _pdaSystem = EntMan.System<PdaSystem>();
    }

    protected override void Open()
    {
        base.Open();

        if (_menu == null)
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
        _menu.ProgramCloseButton.OnPressed += _ => DeactivateActiveCartridge();
        _menu.OnOpenAgentApp += app => SendMessage(new NCAgentOpenAppMessage(app));
        _menu.OnDirectorySearch += query => SendMessage(new NCAgentDirectorySearchMessage(query));
        _menu.OnAddContact += characterId => SendMessage(new NCAgentAddContactMessage(characterId));
        _menu.OnRemoveContact += characterId => SendMessage(new NCAgentRemoveContactMessage(characterId));
        _menu.OnSelectConversation += characterId => SendMessage(new NCAgentSelectConversationMessage(characterId));
        _menu.OnSendDirectMessage += (recipient, body) =>
            SendMessage(new NCAgentSendMessageMessage(recipient, body));
        _menu.OnCreatePost += body => SendMessage(new NCAgentCreatePostMessage(body));
        _menu.OnCreateComment += (postId, body) => SendMessage(new NCAgentCreateCommentMessage(postId, body));
        _menu.OnToggleReaction += postId => SendMessage(new NCAgentToggleReactionMessage(postId));
        _menu.OnStartCall += target => SendMessage(new NCAgentStartCallMessage(target));
        _menu.OnAnswerCall += accept => SendMessage(new NCAgentAnswerCallMessage(accept));
        _menu.OnEndCall += () => SendMessage(new NCAgentEndCallMessage());

        if (EntMan.GetComponentOrNull<PdaBorderColorComponent>(Owner) is not { } colors)
            return;

        _menu.BorderColor = colors.BorderColor;
        _menu.AccentHColor = colors.AccentHColor;
        _menu.AccentVColor = colors.AccentVColor;
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
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
        base.ReceiveMessage(message);
        if (_menu == null)
            return;

        switch (message)
        {
            case NCAgentUpdateMessage update:
                _menu.UpdateAgentData(update);
                break;
            case NCAgentMapUpdateMessage mapUpdate:
                if (EntMan.TryGetComponent<TransformComponent>(Owner, out var transform))
                    _menu.SetMapSource(transform.GridUid);
                _menu.UpdateMapData(mapUpdate);
                break;
        }
    }

    protected override void AttachCartridgeUI(Control cartridgeUiFragment, string? title)
    {
        _menu?.ProgramView.AddChild(cartridgeUiFragment);
        _menu?.ToProgramView(title ?? Loc.GetString("comp-pda-io-program-fallback-title"));
    }

    protected override void DetachCartridgeUI(Control cartridgeUiFragment)
    {
        if (_menu == null)
            return;

        _menu.ToHomeScreen();
        _menu.HideProgramHeader();
        _menu.ProgramView.RemoveChild(cartridgeUiFragment);
    }

    protected override void UpdateAvailablePrograms(List<(EntityUid, CartridgeComponent)> programs)
    {
        _menu?.UpdateAvailablePrograms(programs);
    }
}
