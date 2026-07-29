/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Flight.Components;
using Content.Shared.DoAfter;
using Content.Shared.Toggleable;

namespace Content.Shared._NC.ZLevels.Flight;

public abstract partial class NCSharedZFlightSystem
{
    private void InitializeControllable()
    {
        SubscribeLocalEvent<NCControllableFlightComponent, NCZFlightActionUp>(OnZLevelUp);
        SubscribeLocalEvent<NCControllableFlightComponent, NCZFlightActionDown>(OnZLevelDown);
        SubscribeLocalEvent<NCControllableFlightComponent, ToggleActionEvent>(OnZLevelToggle);

        SubscribeLocalEvent<NCControllableFlightComponent, NCStartFlightDoAfterEvent>(OnStartFlightDoAfter);
        SubscribeLocalEvent<NCControllableFlightComponent, NCFlightStartedEvent>(OnControllableFlightStarted);
        SubscribeLocalEvent<NCControllableFlightComponent, NCFlightStoppedEvent>(OnControllableFlightStopped);
    }

    private void OnControllableFlightStopped(Entity<NCControllableFlightComponent> ent, ref NCFlightStoppedEvent args)
    {
        _actions.SetEnabled(ent.Comp.ZLevelDownActionEntity, false);
        _actions.SetEnabled(ent.Comp.ZLevelUpActionEntity, false);

        // Update toggle action icon state
        if (ent.Comp.ZLevelToggleActionEntity != null)
            _actions.SetToggled(ent.Comp.ZLevelToggleActionEntity, false);
    }

    private void OnControllableFlightStarted(Entity<NCControllableFlightComponent> ent, ref NCFlightStartedEvent args)
    {
        _actions.SetEnabled(ent.Comp.ZLevelDownActionEntity, true);
        _actions.SetEnabled(ent.Comp.ZLevelUpActionEntity, true);

        // Update toggle action icon state
        if (ent.Comp.ZLevelToggleActionEntity != null)
            _actions.SetToggled(ent.Comp.ZLevelToggleActionEntity, true);
    }

    private void OnZLevelUp(Entity<NCControllableFlightComponent> ent, ref NCZFlightActionUp args)
    {
        if (args.Handled)
            return;

        var map = Transform(ent).MapUid;
        if (map is null)
            return;

        if (!TryComp<NCZFlyerComponent>(ent, out var flyerComp))
            return;

        if (!_zLevel.TryMapUp(map.Value, out var mapAbove))
            return;

        flyerComp.TargetMapHeight = mapAbove.Comp.Depth;
        DirtyField(ent, flyerComp, nameof(NCZFlyerComponent.TargetMapHeight));

        args.Handled = true;
    }

    private void OnZLevelDown(Entity<NCControllableFlightComponent> ent, ref NCZFlightActionDown args)
    {
        if (args.Handled)
            return;

        var map = Transform(ent).MapUid;
        if (map is null)
            return;

        if (!TryComp<NCZFlyerComponent>(ent, out var flyerComp))
            return;

        if (!_zLevel.TryMapDown(map.Value, out var mapBelow))
            return;

        flyerComp.TargetMapHeight = mapBelow.Comp.Depth;
        DirtyField(ent, flyerComp, nameof(NCZFlyerComponent.TargetMapHeight));

        args.Handled = true;
    }

    private void OnZLevelToggle(Entity<NCControllableFlightComponent> ent, ref ToggleActionEvent args)
    {
        if (args.Handled)
            return;

        if (!TryComp<NCZFlyerComponent>(ent, out var flyerComp))
            return;

        if (flyerComp.Active)
        {
            DeactivateFlight((ent, flyerComp));
        }
        else
        {
            // If StartFlightDoAfter is set, start a doAfter before activating flight
            if (ent.Comp.StartFlightDoAfter != null)
            {
                //Preventive start flying visuals
                StartFlightVisuals((ent, flyerComp));

                var doAfter = new DoAfterArgs(EntityManager, ent, ent.Comp.StartFlightDoAfter.Value, new NCStartFlightDoAfterEvent(), ent)
                {
                    BreakOnMove = false,
                    BlockDuplicate = true,
                    BreakOnDamage = true,
                    CancelDuplicate = true,
                };

                _doAfter.TryStartDoAfter(doAfter);
            }
            else
            {
                // No delay, activate flight immediately
                TryActivateFlight((ent, flyerComp));
            }
        }

        args.Handled = true;
    }

    private void OnStartFlightDoAfter(Entity<NCControllableFlightComponent> ent, ref NCStartFlightDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
        {
            StopFlightVisuals(ent.Owner);
            return;
        }

        TryActivateFlight(ent.Owner);
        args.Handled = true;
    }
}
