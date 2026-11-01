// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware.Borg;

namespace Content.Server._NC.Medical.Cyberware.Borg;

public sealed partial class NCBorgSystem
{
    private readonly Dictionary<Guid, EntityUid> _liveCores = [];

    public bool CanRestoreCoreIdentity(EntityUid target, Guid coreId)
        => coreId != Guid.Empty && (!_liveCores.TryGetValue(coreId, out var existing) ||
            existing == target || TerminatingOrDeleted(existing));

    public bool TryRestoreCoreIdentity(Entity<NCBorgCoreComponent?> core, Guid coreId)
    {
        if (!Resolve(core, ref core.Comp, false) || !CanRestoreCoreIdentity(core, coreId))
            return false;
        if (_liveCores.GetValueOrDefault(core.Comp.CoreId) == core.Owner)
            _liveCores.Remove(core.Comp.CoreId);
        core.Comp.CoreId = coreId;
        _liveCores[coreId] = core;
        return true;
    }

    private void OnCoreShutdown(Entity<NCBorgCoreComponent> core, ref ComponentShutdown args)
    {
        if (_liveCores.GetValueOrDefault(core.Comp.CoreId) == core.Owner)
            _liveCores.Remove(core.Comp.CoreId);
    }
}
