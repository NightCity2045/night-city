using Content.Server.EUI;
using Content.Server.Ghost;
// NC start - Authoritative Night City physiology adapter for the vanilla defibrillator.
using Content.Server._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Anatomy;
// NC end
using Content.Shared.Medical;
using Content.Shared.Mind;
using Robust.Shared.Player;

namespace Content.Server.Medical;

public sealed partial class DefibrillatorSystem : SharedDefibrillatorSystem
{
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private ISharedPlayerManager _player = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    // NC start
    [Dependency] private NCEmergencyCareSystem _ncEmergency = default!;
    // NC end

    // NC start - The physical vanilla discharge has already consumed its power cell before this hook.
    protected override bool TryHandleAuthoritativeZap(
        EntityUid defibrillator,
        EntityUid target,
        EntityUid user,
        out bool succeeded)
    {
        succeeded = false;
        if (!HasComp<NCMedicalBodyComponent>(target))
            return false;

        succeeded = _ncEmergency.DefibrillateWithExternalPower(target, defibrillator, actor: user).Succeeded;
        return true;
    }
    // NC end

    protected override void OpenReturnToBodyEui(Entity<MindComponent> mind, ICommonSession session)
    {
        _eui.OpenEui(new ReturnToBodyEui(mind, _mind, _player), session);
    }
}
