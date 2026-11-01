// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Paper;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private PaperSystem _servicePaper = default!;

    private string ServiceOwner(EntityUid body)
    {
        return TryComp<NCMedicalPersistenceOwnerComponent>(body, out var owner) &&
               owner.Kind != NCMedicalStableOwnerKind.Unassigned
            ? $"{(byte) owner.Kind}:{owner.Key}" : string.Empty;
    }

    private void RecordService(EntityUid uid, NCCyberwareServiceOperation operation, Guid resolution, EntityUid? body = null)
    {
        if (!TryComp<NCCyberwareComponent>(uid, out var module))
            return;
        if (!NCCyberwareServiceHistory.TryAppend(module.Service, module.Service.Revision, resolution,
                operation, body is { } patient ? ServiceOwner(patient) : string.Empty))
            Log.Warning($"Cyberware {module.StableInstanceId} rejected service audit {resolution} at revision {module.Service.Revision}.");
    }

    public void RecordAnatomicalTransfer(EntityUid uid, EntityUid body, bool installed)
    {
        RecordService(uid, installed ? NCCyberwareServiceOperation.Installed : NCCyberwareServiceOperation.Removed,
            Guid.NewGuid(), body);
    }

    public bool FirmwareAllowsActiveModes(EntityUid uid)
    {
        return TryComp<NCCyberwareComponent>(uid, out var module) &&
               _prototypes.TryIndex(module.Service.Firmware, out var firmware) && !firmware.InhibitActiveModes;
    }

    public bool CanFlashFirmware(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid card,
        EntityUid expectedModule, uint expectedRevision)
    {
        return CanOperateWorkbench(bench, user) && TryGetServiceWorkpiece(bench, out var uid) &&
               uid == expectedModule && _assemblyHands.IsHolding(user, card) &&
               TryComp<NCCyberwareFirmwareCardComponent>(card, out var media) &&
               _prototypes.TryIndex(media.Firmware, out var firmware) &&
               TryComp<NCCyberwareComponent>(uid, out var module) &&
               NCCyberwareServiceHistory.Valid(module.Service) && module.Service.Revision == expectedRevision &&
               expectedRevision < uint.MaxValue && module.Service.Firmware != media.Firmware &&
               (firmware.CompatibleProfiles.Count == 0 || firmware.CompatibleProfiles.Contains(module.Profile));
    }

    /// <summary>Physical bench and held media only. A saved revision cannot authorize a later configuration.</summary>
    public bool TryFlashFirmware(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid card,
        EntityUid expectedModule, uint expectedRevision)
    {
        if (!CanFlashFirmware(bench, user, card, expectedModule, expectedRevision) ||
            !Resolve(bench, ref bench.Comp, false) || !_cells.TryGetBatteryFromSlot(bench.Owner, out var battery) ||
            !_battery.TryUseCharge(battery.Value.AsNullable(), bench.Comp.TestEnergy))
            return false;
        var module = Comp<NCCyberwareComponent>(expectedModule);
        module.Service.Firmware = Comp<NCCyberwareFirmwareCardComponent>(card).Firmware;
        if (TryComp<NCCyberwareAssemblyComponent>(expectedModule, out var assembly)) assembly.ValidatedRevision = null;
        RecordService(expectedModule, NCCyberwareServiceOperation.Firmware, Guid.NewGuid());
        _assemblyPopup.PopupEntity(Loc.GetString("nc-cyberware-service-flashed"), bench, user);
        return true;
    }

    public bool TryWriteServiceDocument(Entity<NCCyberwareWorkbenchComponent?> bench, EntityUid user, EntityUid paper)
    {
        if (!CanOperateWorkbench(bench, user) || !TryGetServiceWorkpiece(bench, out var uid) ||
            !_assemblyHands.IsHolding(user, paper) || !TryComp<PaperComponent>(paper, out var sheet) ||
            sheet.EditingDisabled || sheet.Content.Length != 0 || sheet.StampedBy.Count != 0 ||
            !TryCaptureOrganState(uid, out var state) || !Resolve(bench, ref bench.Comp, false))
            return false;
        var service = Comp<NCCyberwareComponent>(uid).Service;
        TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly);
        if (!_prototypes.TryIndex(service.Firmware, out var firmware))
            return false;
        var content = Loc.GetString("nc-cyberware-service-document", ("serial", state.StableInstanceId.ToString("D")),
            ("revision", assembly?.ConfigurationRevision ?? 0), ("service", service.Revision),
            ("firmware", Loc.GetString(firmware.Name)), ("integrity", state.CurrentIntegrity),
            ("ceiling", NCCyberwareCondition.ServiceCeiling(state.MaximumIntegrity, state.PermanentDamage)),
            ("heat", state.Heat), ("tested", Loc.GetString(assembly != null && assembly.ValidatedRevision == assembly.ConfigurationRevision
                ? "nc-cyberware-bench-tested" : "nc-cyberware-bench-untested")));
        if (content.Length > sheet.ContentSize || !_cells.TryGetBatteryFromSlot(bench.Owner, out var battery) ||
            !_battery.TryUseCharge(battery.Value.AsNullable(), bench.Comp.TestEnergy))
            return false;
        _servicePaper.SetContent((paper, sheet), content);
        return true;
    }

    public void PrepareLegacyServiceState(EntityUid body, NCMedicalStateSnapshot snapshot)
    {
        var live = new Dictionary<Guid, NCCyberwareServiceState>();
        foreach (var uid in CollectCyberware(body))
        {
            var module = Comp<NCCyberwareComponent>(uid);
            live.TryAdd(module.StableInstanceId, module.Service);
        }
        foreach (var state in snapshot.CyberwareModules.Concat(snapshot.Organs.Values
                     .Where(organ => organ.Cyberware != null).Select(organ => organ.Cyberware!)))
            state.Service ??= live.TryGetValue(state.StableInstanceId, out var service)
                ? NCCyberwareServiceHistory.Clone(service) : new NCCyberwareServiceState();
    }
}
