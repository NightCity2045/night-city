// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Diagnostics.Detail;

/// <summary>
/// Reduces applied medical events into one bounded in-round patient timeline without persistence or scans.
/// </summary>
public sealed partial class NCMedicalHistorySystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMedicalHistoryComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCWoundCreatedEvent>(OnWoundCreated);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCTreatmentChangedEvent>(OnTreatment);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCEmergencyCareChangedEvent>(OnEmergencyCare);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCPharmacologyChangedEvent>(OnPharmacology);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCSurgeryStartedEvent>(OnSurgeryStarted);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCSurgeryStepResolvedEvent>(OnSurgeryStep);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCSurgeryCompletedEvent>(OnSurgeryCompleted);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCSurgeryAbortedEvent>(OnSurgeryAborted);
        SubscribeLocalEvent<NCMedicalHistoryComponent, NCAutopsyRecordedEvent>(OnAutopsyRecorded);
    }

    private void OnInit(Entity<NCMedicalHistoryComponent> entity, ref ComponentInit args)
    {
        entity.Comp.Records.Clear();
    }

    private void OnWoundCreated(Entity<NCMedicalHistoryComponent> entity, ref NCWoundCreatedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.Injury,
            Zone = args.TargetZone,
            WoundSeverity = args.Severity,
            WoundType = args.WoundType,
        });

    private void OnTreatment(Entity<NCMedicalHistoryComponent> entity, ref NCTreatmentChangedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.Treatment,
            Zone = args.Zone,
            Treatment = args.Kind,
        });

    private void OnEmergencyCare(Entity<NCMedicalHistoryComponent> entity, ref NCEmergencyCareChangedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.EmergencyCare,
            EmergencyCare = args.Kind,
        });

    private void OnPharmacology(Entity<NCMedicalHistoryComponent> entity, ref NCPharmacologyChangedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.Medication,
            Drug = args.Profile,
        });

    private void OnSurgeryStarted(Entity<NCMedicalHistoryComponent> entity, ref NCSurgeryStartedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.SurgeryStarted,
            Procedure = args.Procedure,
        });

    private void OnSurgeryStep(Entity<NCMedicalHistoryComponent> entity, ref NCSurgeryStepResolvedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.SurgeryStep,
            SurgeryStep = args.Step,
            SurgeryOutcome = args.Outcome,
        });

    private void OnSurgeryCompleted(Entity<NCMedicalHistoryComponent> entity, ref NCSurgeryCompletedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.SurgeryCompleted,
            Procedure = args.Procedure,
        });

    private void OnSurgeryAborted(Entity<NCMedicalHistoryComponent> entity, ref NCSurgeryAbortedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.SurgeryAborted,
        });

    private void OnAutopsyRecorded(Entity<NCMedicalHistoryComponent> entity, ref NCAutopsyRecordedEvent args) =>
        Add(entity.Comp, new NCMedicalHistoryRecord
        {
            Timestamp = _timing.CurTime,
            Kind = NCMedicalHistoryKind.Autopsy,
            AutopsyRegion = args.Region,
            AutopsyWoundCount = (byte) Math.Clamp(args.WoundCount, 0, byte.MaxValue),
            AutopsyWoundTypes = args.WoundTypes.Distinct().Order().ToList(),
        });

    private static void Add(NCMedicalHistoryComponent component, NCMedicalHistoryRecord record)
    {
        var capacity = Math.Clamp(component.Capacity, 1, 512);
        component.Records.Add(record);
        while (component.Records.Count > capacity)
            component.Records.RemoveAt(0);
    }
}
