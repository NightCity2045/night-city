/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server.Actions;
using Content.Shared._NC.ZLevels.Flight;
using Content.Shared._NC.ZLevels.Flight.Components;

namespace Content.Server._NC.ZLevels.Flight;

public sealed partial class NCZFlightSystem : NCSharedZFlightSystem
{
    [Dependency] private ActionsSystem _actions = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCControllableFlightComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCControllableFlightComponent, ComponentRemove>(OnRemove);
    }

    private void OnRemove(Entity<NCControllableFlightComponent> ent, ref ComponentRemove args)
    {
        _actions.RemoveAction(ent.Comp.ZLevelUpActionEntity);
        _actions.RemoveAction(ent.Comp.ZLevelDownActionEntity);
        _actions.RemoveAction(ent.Comp.ZLevelToggleActionEntity);
    }

    private void OnMapInit(Entity<NCControllableFlightComponent> ent, ref MapInitEvent args)
    {
        if (!ZPhyzQuery.TryComp(ent, out var zPhys))
            return;

        if (!TryComp<NCZFlyerComponent>(ent.Owner, out var flyerComp))
            return;

        SetTargetHeight(ent.Owner, zPhys.CurrentZLevel);

        _actions.AddAction(ent, ref ent.Comp.ZLevelUpActionEntity, ent.Comp.UpActionProto);
        _actions.AddAction(ent, ref ent.Comp.ZLevelDownActionEntity, ent.Comp.DownActionProto);
        _actions.AddAction(ent, ref ent.Comp.ZLevelToggleActionEntity, ent.Comp.ToggleActionProto);

        _actions.SetEnabled(ent.Comp.ZLevelDownActionEntity, flyerComp.Active);
        _actions.SetEnabled(ent.Comp.ZLevelUpActionEntity, flyerComp.Active);
    }
}
