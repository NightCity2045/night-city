// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Cyberware;
using Content.Server.Administration;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Admin;

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCCyberwareStatusCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    public string Command => "nccyberwarestatus";
    public string Description => Loc.GetString("nc-cyberware-service-admin-description");
    public string Help => Loc.GetString("nc-cyberware-service-admin-help");

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !NetEntity.TryParse(args[0], out var net) ||
            !_entities.TryGetEntity(net, out var entity) || entity is not { } uid)
        {
            shell.WriteError(Help);
            return;
        }
        var system = _entities.System<NCCyberwareSystem>();
        if (_entities.System<Content.Server._NC.Medical.Humanity.NCHumanitySystem>()
            .TryCapturePersistentState(uid, out var humanity) && humanity != null)
            shell.WriteLine(Loc.GetString("nc-cyberware-service-admin-humanity", ("current", humanity.Current),
                ("burden", humanity.Burden), ("stress", humanity.Stress), ("stage", (int) humanity.Stage),
                ("adaptations", humanity.Adaptation.Count), ("impulses", humanity.RecentImpulses.Count)));
        var modules = _entities.HasComponent<NCCyberwareComponent>(uid) ? new List<EntityUid> { uid }
            : system.CollectCyberware(uid);
        if (modules.Count == 0)
            shell.WriteLine(Loc.GetString("nc-cyberware-service-admin-empty"));
        foreach (var module in modules)
        {
            if (!system.TryCaptureOrganState(module, out var state) || state.Service is not { } service) continue;
            var firmware = _prototypes.TryIndex(service.Firmware, out var profile)
                ? Loc.GetString(profile.Name) : Loc.GetString("nc-cyberware-service-invalid");
            shell.WriteLine(Loc.GetString("nc-cyberware-service-admin-item", ("entity", _entities.ToPrettyString(module).ToString()),
                ("serial", state.StableInstanceId.ToString("D")), ("revision", service.Revision), ("firmware", firmware),
                ("integrity", state.CurrentIntegrity), ("defect", state.PermanentDamage), ("heat", state.Heat),
                ("omitted", service.OmittedEvents)));
            foreach (var entry in service.History)
                shell.WriteLine(Loc.GetString("nc-cyberware-service-admin-entry", ("revision", entry.Revision),
                    ("resolution", entry.ResolutionId.ToString("D")), ("owner", entry.Owner),
                    ("operation", Loc.GetString($"nc-cyberware-service-operation-{(byte) entry.Operation}"))));
        }
    }
}
