/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Server.GameStates;

namespace Content.Server._NC.ZLevels.PVS;

public sealed partial class NCPvsOverrideSystem : EntitySystem
{
    [Dependency] private PvsOverrideSystem _pvs = default!;
    public override void Initialize()
    {
        SubscribeLocalEvent<NCPvsOverrideComponent, ComponentStartup>(OnPvsStartup);
        SubscribeLocalEvent<NCPvsOverrideComponent, ComponentShutdown>(OnPvsShutdown);
    }

    private void OnPvsShutdown(Entity<NCPvsOverrideComponent> ent, ref ComponentShutdown args)
    {
        _pvs.RemoveGlobalOverride(ent);
    }

    private void OnPvsStartup(Entity<NCPvsOverrideComponent> ent, ref ComponentStartup args)
    {
        _pvs.AddGlobalOverride(ent);
    }
}
