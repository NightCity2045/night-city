// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.DeviceLinking.Systems;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Buckle.Components;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Diagnostics.Scanner;

/// <summary>
/// Maintains an event-driven link to the vanilla operating table. It never scans the world after map startup and
/// never stores the patient: every diagnostic request reads the authoritative occupant from the table strap.
/// </summary>
public sealed partial class NCBodyScannerConsoleSystem : EntitySystem
{
    public static readonly ProtoId<SourcePortPrototype> TablePort = "NCOperatingTableSender";
    public static readonly ProtoId<SinkPortPrototype> TableReceiverPort = "NCOperatingTableReceiver";

    [Dependency] private DeviceLinkSystem _deviceLink = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCBodyScannerConsoleComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<NCBodyScannerConsoleComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCBodyScannerConsoleComponent, NewLinkEvent>(OnNewLink);
        SubscribeLocalEvent<NCBodyScannerConsoleComponent, PortDisconnectedEvent>(OnPortDisconnected);
    }

    /// <summary>
    /// Resolves the current patient through the linked operating table. This is called for initial scans and every
    /// live refresh, so unbuckling or replacing a patient immediately invalidates the old observation.
    /// </summary>
    public bool TryGetPatient(
        Entity<NCBodyScannerConsoleComponent> console,
        out EntityUid patient,
        out NCDiagnosticAccessFailure failure)
    {
        patient = default;
        failure = NCDiagnosticAccessFailure.OperatingTableDisconnected;
        if (console.Comp.ConnectedTable is not { Valid: true } tableUid ||
            !TryComp<StrapComponent>(tableUid, out var table) ||
            !TryComp<NCSurgeryFacilityComponent>(tableUid, out _) ||
            !TryComp<DeviceLinkSinkComponent>(tableUid, out var sink))
        {
            return false;
        }

        var sinkPorts = sink.Ports;
        if (!sinkPorts.Contains(TableReceiverPort))
            return false;

        failure = NCDiagnosticAccessFailure.OperatingTableEmpty;
        EntityUid? selected = null;
        foreach (var occupant in table.BuckledEntities)
        {
            if (!Exists(occupant) || !HasComp<NCMedicalBodyComponent>(occupant))
                continue;

            // An operating table normally contains one patient. Stable selection keeps malformed multi-occupant
            // states deterministic without ever accepting a client-provided entity.
            if (selected is null || occupant.Id < selected.Value.Id)
                selected = occupant;
        }

        if (selected is not { } selectedPatient)
            return false;

        patient = selectedPatient;
        failure = NCDiagnosticAccessFailure.None;
        return true;
    }

    private void OnComponentInit(
        Entity<NCBodyScannerConsoleComponent> console,
        ref ComponentInit args)
    {
        _deviceLink.EnsureSourcePorts(console.Owner, TablePort);
    }

    private void OnMapInit(
        Entity<NCBodyScannerConsoleComponent> console,
        ref MapInitEvent args)
    {
        if (!TryComp<DeviceLinkSourceComponent>(console.Owner, out var source))
            return;

        var outputs = source.Outputs;
        // A source port is expected to have one operating table. If a malformed map links several, choosing the
        // lowest UID keeps the authoritative result stable until the wiring is corrected.
        EntityUid? selected = null;
        if (outputs.TryGetValue(TablePort, out var sinks))
        {
            foreach (var sink in sinks)
            {
                if (!IsDiagnosticTable(sink) || selected is { } current && current.Id <= sink.Id)
                    continue;

                selected = sink;
            }
        }

        if (selected is { })
        {
            console.Comp.ConnectedTable = selected;
            return;
        }

        // Maps predating this feature do not serialize a body-computer link. Establish a real device link to the
        // nearest compatible table through one bounded spatial query; subsequent state stays event-driven.
        var origin = _transform.GetWorldPosition(console.Owner);
        var bestDistance = float.MaxValue;
        foreach (var table in _lookup.GetEntitiesInRange<StrapComponent>(
                     Transform(console.Owner).Coordinates,
                     source.Range))
        {
            if (!IsDiagnosticTable(table.Owner))
                continue;

            var distance = (origin - _transform.GetWorldPosition(table.Owner)).LengthSquared();
            if (distance > bestDistance ||
                MathHelper.CloseTo(distance, bestDistance) && selected is { } current && current.Id <= table.Owner.Id)
            {
                continue;
            }

            selected = table.Owner;
            bestDistance = distance;
        }

        if (selected is { } nearest)
            _deviceLink.LinkDefaults(null, console.Owner, nearest, source);
    }

    private void OnNewLink(
        Entity<NCBodyScannerConsoleComponent> console,
        ref NewLinkEvent args)
    {
        if (args.Source == console.Owner &&
            args.SourcePort == TablePort.Id &&
            IsDiagnosticTable(args.Sink))
        {
            console.Comp.ConnectedTable = args.Sink;
        }
    }

    private void OnPortDisconnected(
        Entity<NCBodyScannerConsoleComponent> console,
        ref PortDisconnectedEvent args)
    {
        if (args.Port == TablePort.Id)
            console.Comp.ConnectedTable = null;
    }

    private bool IsDiagnosticTable(EntityUid uid)
    {
        if (Deleted(uid) ||
            !HasComp<NCSurgeryFacilityComponent>(uid) ||
            !HasComp<StrapComponent>(uid) ||
            !TryComp<DeviceLinkSinkComponent>(uid, out var sink))
        {
            return false;
        }

        var ports = sink.Ports;
        return ports.Contains(TableReceiverPort);
    }
}
