// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    /// <summary>Independent trusted demand; it does not enable or disable a physical ability.</summary>
    public bool TrySetComputeLoad(EntityUid uid, float load)
    {
        if (!float.IsFinite(load) || load is < 0 or > 1 || !TryComp<NCCyberwareComponent>(uid, out var module) ||
            !TryGetProfile(module.Profile, out var profile)) return false;
        if (TryResolveOwningBody(uid, out var body)) TryAdvanceBodyPower(body, _timing.CurTime);
        else if (load != 0) return false;
        module.ComputeLoad = load;
        Schedule(uid, profile, true);
        return true;
    }
}
