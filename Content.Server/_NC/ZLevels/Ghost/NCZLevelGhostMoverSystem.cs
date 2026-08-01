/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Ghost;
using Content.Shared.Actions;

namespace Content.Server._NC.ZLevels.Ghost;

public sealed partial class NCZLevelGhostMoverSystem : NCSharedZLevelGhostMoverSystem
{
    [Dependency] private SharedActionsSystem _actions = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCZLevelGhostMoverComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCZLevelGhostMoverComponent, ComponentRemove>(OnRemove);
    }

    private void OnMapInit(Entity<NCZLevelGhostMoverComponent> ent, ref MapInitEvent args)
    {
        _actions.AddAction(ent, ref ent.Comp.ZLevelUpActionEntity, ent.Comp.UpActionProto);
        _actions.AddAction(ent, ref ent.Comp.ZLevelDownActionEntity, ent.Comp.DownActionProto);
    }

    private void OnRemove(Entity<NCZLevelGhostMoverComponent> ent, ref ComponentRemove args)
    {
        _actions.RemoveAction(ent.Comp.ZLevelUpActionEntity);
        _actions.RemoveAction(ent.Comp.ZLevelDownActionEntity);
    }
}
