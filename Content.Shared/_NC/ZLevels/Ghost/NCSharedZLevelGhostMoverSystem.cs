/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */


using Content.Shared._NC.ZLevels.Core.EntitySystems;

namespace Content.Shared._NC.ZLevels.Ghost;

public abstract partial class NCSharedZLevelGhostMoverSystem : EntitySystem
{
    [Dependency] private NCSharedZLevelsSystem _zLevel = null!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCZLevelGhostMoverComponent, NCZLevelActionUp>(OnZLevelUp);
        SubscribeLocalEvent<NCZLevelGhostMoverComponent, NCZLevelActionDown>(OnZLevelDown);
    }

    private void OnZLevelDown(Entity<NCZLevelGhostMoverComponent> ent, ref NCZLevelActionDown args)
    {
        if (args.Handled)
            return;

        args.Handled = _zLevel.TryMoveDown(ent);
    }

    private void OnZLevelUp(Entity<NCZLevelGhostMoverComponent> ent, ref NCZLevelActionUp args)
    {
        if (args.Handled)
            return;

        args.Handled = _zLevel.TryMoveUp(ent);
    }
}
