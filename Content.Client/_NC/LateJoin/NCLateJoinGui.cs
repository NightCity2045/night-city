using System.Numerics;
using Content.Client.GameTicking.Managers;
using Content.Client.UserInterface.Controls;
using Content.Shared._NC.Roles;
using Robust.Client.Console;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Utility;

namespace Content.Client._NC.LateJoin;

/// <summary>
/// Late-join window for the single-role Night City flow.
/// It deliberately exposes entry locations, but never the technical Citizen job.
/// </summary>
public sealed partial class NCLateJoinGui : DefaultWindow
{
    [Dependency] private IClientConsoleHost _consoleHost = default!;
    [Dependency] private IEntitySystemManager _entitySystems = default!;

    public NCLateJoinGui()
    {
        IoCManager.InjectDependencies(this);

        Title = Loc.GetString("nc-late-join-title");
        MinSize = SetSize = new Vector2(360, 180);

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 8,
            Margin = new Thickness(8),
        };

        content.AddChild(new Label
        {
            Text = Loc.GetString("nc-late-join-description"),
        });

        var ticker = _entitySystems.GetEntitySystem<ClientGameTicker>();
        if (ticker.StationNames.Count == 0)
        {
            content.AddChild(new Label
            {
                Text = Loc.GetString("nc-late-join-no-stations"),
            });
        }

        foreach (var (station, stationName) in ticker.StationNames)
        {
            var button = new Button
            {
                Text = Loc.GetString("nc-late-join-station-button", ("station", stationName)),
            };

            button.OnPressed += _ =>
            {
                _consoleHost.ExecuteCommand(
                    $"joingame {CommandParsing.Escape(NCJobIds.Citizen.Id)} {station}");
                Close();
            };

            content.AddChild(button);
        }

        ContentsContainer.AddChild(content);
    }
}
