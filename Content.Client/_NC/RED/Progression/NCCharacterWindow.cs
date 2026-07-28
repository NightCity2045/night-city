using Content.Shared._NC.RED.Progression;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Numerics;

namespace Content.Client._NC.RED.Progression;

/// <summary>
/// Readable client view of server-authoritative persistent state.
/// Skill buttons submit requests; they never mutate the displayed rank optimistically.
/// </summary>
public sealed class NCCharacterWindow : DefaultWindow
{
    private readonly IPrototypeManager _prototypes;
    private readonly Action<string, int> _allocate;
    private readonly BoxContainer _content;

    public NCCharacterWindow(IPrototypeManager prototypes, Action<string, int> allocate)
    {
        _prototypes = prototypes;
        _allocate = allocate;
        Title = Loc.GetString("nc-character-window-title");
        MinSize = SetSize = new Vector2(460, 520);

        var scroll = new ScrollContainer();
        _content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 6,
            Margin = new Thickness(10),
        };
        scroll.AddChild(_content);
        ContentsContainer.AddChild(scroll);
    }

    public void UpdateState(NCProgressionStateEvent state)
    {
        _content.RemoveAllChildren();
        _content.AddChild(new Label
        {
            Text = Loc.GetString("nc-character-window-summary",
                ("level", state.Level),
                ("rounds", state.CompletedRounds),
                ("available", state.TotalSkillPoints - state.SpentSkillPoints),
                ("balance", state.BankBalance)),
        });
        _content.AddChild(new Label
        {
            Text = Loc.GetString("nc-character-window-assets",
                ("properties", state.PropertyCount),
                ("businesses", state.BusinessCount)),
        });
        _content.AddChild(new Label
        {
            Text = Loc.GetString("nc-character-window-position",
                ("position", GetPositionName(state.PositionPrototypeId))),
        });

        if (state.Error != null)
            _content.AddChild(new Label { Text = Loc.GetString(state.Error) });

        foreach (var skill in _prototypes.EnumeratePrototypes<NCRedSkillPrototype>()
                     .OrderBy(skill => Loc.GetString(skill.Name)))
        {
            state.Skills.TryGetValue(skill.ID, out var rank);
            var button = new Button
            {
                Text = Loc.GetString("nc-character-window-skill",
                    ("skill", Loc.GetString(skill.Name)),
                    ("rank", rank),
                    ("max", skill.MaxRank),
                    ("cost", skill.CostPerRank)),
                Disabled = rank >= skill.MaxRank ||
                           state.TotalSkillPoints - state.SpentSkillPoints < skill.CostPerRank,
                ToolTip = Loc.GetString(skill.Description),
            };
            var requestedRank = rank + 1;
            button.OnPressed += _ => _allocate(skill.ID, requestedRank);
            _content.AddChild(button);
        }
    }

    private string GetPositionName(string? prototypeId)
    {
        if (prototypeId == null ||
            !_prototypes.TryIndex<Content.Shared._NC.Organizations.NCPositionPrototype>(
                prototypeId,
                out var position))
            return Loc.GetString("nc-character-window-unemployed");
        return Loc.GetString(position.Name);
    }
}
