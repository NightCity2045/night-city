// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Text.Json.Nodes;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Paper;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;

namespace Content.Server._NC.Diagnostics;

public sealed partial class NCDiagnosticsSystem
{
    [Dependency] private SharedBatterySystem _cyberwareBattery = default!;
    /// <summary>Explicit read-only local diagnostic projection. Never included in normal client replication.</summary>
    private void AppendCyberwareProjection(EntityUid uid, JsonObject result)
    {
        if (TryComp<NCCyberwareComponent>(uid, out var module))
            result["cyberwareService"] = new JsonObject
            {
                ["instanceId"] = module.StableInstanceId.ToString("D"),
                ["firmware"] = module.Service.Firmware.Id,
                ["revision"] = module.Service.Revision,
                ["historyEntries"] = module.Service.History.Count,
            };
        if (TryComp<PaperComponent>(uid, out var paper))
            result["paper"] = new JsonObject
            {
                ["hasContent"] = paper.Content.Length > 0,
                ["content"] = paper.Content.Length <= 4096 ? paper.Content : paper.Content[..4096],
                ["truncated"] = paper.Content.Length > 4096,
                ["editingDisabled"] = paper.EditingDisabled,
            };
        if (TryComp<BatteryComponent>(uid, out var battery))
            result["battery"] = new JsonObject { ["charge"] = _cyberwareBattery.GetCharge((uid, battery)) };
    }
}
#endif
