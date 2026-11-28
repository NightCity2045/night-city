using System.Linq;
using Content.Client._NC.Fixers; // NC
using Content.Client._NC.Skill; // NC
using Content.Client.CharacterInfo;
using Content.Client.Gameplay;
using Content.Client.Stylesheets;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Systems.Character.Windows;
using Content.Client.UserInterface.Systems.Objectives.Controls;
using Content.Shared._NC.Fixers; // NC
using Content.Shared._NC.Skill; // NC
using Content.Shared.Input;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using JetBrains.Annotations;
using Robust.Client.Console; // NC
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input.Binding;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using static Content.Client.CharacterInfo.CharacterInfoSystem;
using static Robust.Client.UserInterface.Controls.BaseButton;

namespace Content.Client.UserInterface.Systems.Character;

[UsedImplicitly]
public sealed partial class CharacterUIController : UIController, IOnStateEntered<GameplayState>, IOnStateExited<GameplayState>,
    IOnSystemChanged<CharacterInfoSystem>, IOnSystemChanged<NCSkillClientSystem>, IOnSystemChanged<NCStreetReputationClientSystem> // NC
{
    [Dependency] private IEntityManager _ent = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IClientConsoleHost _console = default!; // NC

    [UISystemDependency] private readonly CharacterInfoSystem _characterInfo = default!;
    [UISystemDependency] private readonly SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<MindRoleTypeChangedEvent>(OnRoleTypeChanged);
    }

    private CharacterWindow? _window;
    private MenuButton? CharacterButton => UIManager.GetActiveUIWidgetOrNull<MenuBar.Widgets.GameTopMenuBar>()?.CharacterButton;

    public void OnStateEntered(GameplayState state)
    {
        DebugTools.Assert(_window == null);

        _window = UIManager.CreateWindow<CharacterWindow>();
        LayoutContainer.SetAnchorPreset(_window, LayoutContainer.LayoutPreset.CenterTop);

        _window.OnClose += DeactivateButton;
        _window.OnOpen += ActivateButton;
        _window.SkillsPressed += OpenSkills; // NC
        _window.Relocalized += UpdateRoleType; // NC

        CommandBinds.Builder
            .Bind(ContentKeyFunctions.OpenCharacterMenu,
                InputCmdHandler.FromDelegate(_ => ToggleWindow()))
            .Register<CharacterUIController>();
    }

    public void OnStateExited(GameplayState state)
    {
        if (_window != null)
        {
            _window.Close();
            _window = null;
        }

        CommandBinds.Unregister<CharacterUIController>();
    }

    public void OnSystemLoaded(CharacterInfoSystem system)
    {
        system.OnCharacterUpdate += CharacterUpdated;
        _player.LocalPlayerDetached += CharacterDetached;
    }

    public void OnSystemUnloaded(CharacterInfoSystem system)
    {
        system.OnCharacterUpdate -= CharacterUpdated;
        _player.LocalPlayerDetached -= CharacterDetached;
    }

    // NC-Edit-Start
    public void OnSystemLoaded(NCSkillClientSystem system)
    {
        system.ProgressChanged += RefreshProgress;
    }

    public void OnSystemUnloaded(NCSkillClientSystem system)
    {
        system.ProgressChanged -= RefreshProgress;
    }

    public void OnSystemLoaded(NCStreetReputationClientSystem system)
    {
        system.ReputationChanged += RefreshProgress;
    }

    public void OnSystemUnloaded(NCStreetReputationClientSystem system)
    {
        system.ReputationChanged -= RefreshProgress;
    }

    private void RefreshProgress()
    {
        if (_window is not { IsOpen: true } window)
            return;

        if (_ent.TryGetComponent<NCSkillPermissionsComponent>(_player.LocalEntity, out var skills))
            window.SetProgress(skills.Experience, skills.Learned.Length);
        else
            window.SetProgress(-1, 0);

        window.SetReputation(_ent.TryGetComponent<NCStreetReputationComponent>(_player.LocalEntity, out var reputation)
            ? reputation.Reputation
            : null);
    }

    private void OpenSkills() => _console.RemoteExecuteCommand(null, "ncskills");
    // NC-Edit-End

    public void UnloadButton()
    {
        if (CharacterButton == null)
        {
            return;
        }

        CharacterButton.OnPressed -= CharacterButtonPressed;
    }

    public void LoadButton()
    {
        if (CharacterButton == null)
        {
            return;
        }

        CharacterButton.OnPressed += CharacterButtonPressed;
    }

    private void DeactivateButton()
    {
        if (CharacterButton == null)
        {
            return;
        }

        CharacterButton.Pressed = false;
    }

    private void ActivateButton()
    {
        if (CharacterButton == null)
        {
            return;
        }

        CharacterButton.Pressed = true;
    }

    private void CharacterUpdated(CharacterData data)
    {
        if (_window == null || _player.LocalEntity != data.Entity) // NC
        {
            return;
        }

        var (entity, job, objectives, briefing, entityName) = data;

        // NC-Edit-Start
        _window.SetIdentity(entity, entityName, job);
        UpdateRoleType();
        _window.BriefingSection.Visible = !string.IsNullOrWhiteSpace(briefing);
        _window.BriefingText.SetMessage(briefing ?? string.Empty);
        _window.Objectives.RemoveAllChildren();
        var controls = _characterInfo.GetCharacterInfoControls(entity);
        var empty = objectives.Count == 0 && !_window.BriefingSection.Visible && controls.Count == 0;
        _window.ObjectivesSection.Visible = objectives.Count > 0 || empty;
        _window.EmptyObjectives.Visible = empty;
        // NC-Edit-End

        foreach (var (groupId, conditions) in objectives)
        {
            var objectiveControl = new BoxContainer // NC
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                SeparationOverride = 4 // NC
            };


            var objectiveText = new FormattedMessage();
            objectiveText.TryAddMarkup(groupId, out _);

            var objectiveLabel = new RichTextLabel
            {
                StyleClasses = { StyleClass.TooltipTitle }
            };
            objectiveLabel.SetMessage(objectiveText);

            objectiveControl.AddChild(objectiveLabel);

            foreach (var condition in conditions)
            {
                var conditionControl = new ObjectiveConditionsControl();
                conditionControl.ProgressTexture.Texture = _sprite.Frame0(condition.Icon);
                conditionControl.ProgressTexture.Progress = condition.Progress;
                var titleMessage = new FormattedMessage();
                var descriptionMessage = new FormattedMessage();
                titleMessage.AddText(condition.Title);
                descriptionMessage.AddText(condition.Description);

                conditionControl.Title.SetMessage(titleMessage);
                conditionControl.Description.SetMessage(descriptionMessage);

                objectiveControl.AddChild(conditionControl);
            }

            _window.Objectives.AddChild(objectiveControl);
        }

        // NC-Edit-Start
        _window.AdditionalInfo.RemoveAllChildren();
        _window.AdditionalSection.Visible = controls.Count > 0;
        foreach (var control in controls)
        {
            _window.AdditionalInfo.AddChild(control);
        }
        // NC-Edit-End
    }

    private void OnRoleTypeChanged(MindRoleTypeChangedEvent ev, EntitySessionEventArgs _)
    {
        UpdateRoleType();
    }

    private void UpdateRoleType()
    {
        if (_window == null || !_window.IsOpen)
            return;

        // NC-Edit-Start
        if (!_ent.TryGetComponent<MindContainerComponent>(_player.LocalEntity, out var container) ||
            !_ent.TryGetComponent<MindComponent>(container.Mind, out var mind))
        {
            _window.RoleType.Visible = false;
            return;
        }
        // NC-Edit-End

        if (!_prototypeManager.TryIndex(mind.RoleType, out var proto))
            Log.Error($"Player '{_player.LocalSession}' has invalid Role Type '{mind.RoleType}'. Displaying default instead");

        _window.RoleType.Text = Loc.GetString(proto?.Name ?? "role-type-crew-aligned-name");
        _window.RoleType.FontColorOverride = proto?.Color ?? Color.White;
        _window.RoleType.Visible = true; // NC
    }

    private void CharacterDetached(EntityUid uid)
    {
        CloseWindow();
        _window?.ResetCharacter(); // NC
    }

    private void CharacterButtonPressed(ButtonEventArgs args)
    {
        ToggleWindow();
    }

    private void CloseWindow()
    {
        _window?.Close();
    }

    private void ToggleWindow()
    {
        if (_window == null)
            return;

        CharacterButton?.SetClickPressed(!_window.IsOpen);

        if (_window.IsOpen)
        {
            CloseWindow();
        }
        else
        {
            _window.Open();
            RefreshProgress(); // NC
            _characterInfo.RequestCharacterInfo();
        }
    }
}
