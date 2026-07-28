using Robust.Shared.Console;

namespace Content.Client._NC.RED.Progression;

public sealed class NCCharacterCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;

    public string Command => "nc_character";
    public string Description => Loc.GetString("nc-character-command-description");
    public string Help => Command;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _systems.GetEntitySystem<NCCharacterSystem>().Open();
    }
}
