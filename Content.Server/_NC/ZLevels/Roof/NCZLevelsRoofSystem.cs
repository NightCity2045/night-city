/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Roof;

namespace Content.Server._NC.ZLevels.Roof;

public sealed partial class NCZLevelsRoofSystem : NCSharedZLevelsRoofSystem
{
    private readonly HashSet<Vector2i> _roofMap = new();

    public override void Initialize()
    {
        base.Initialize();

        InitMaps();
        InitGrids();
    }
}
