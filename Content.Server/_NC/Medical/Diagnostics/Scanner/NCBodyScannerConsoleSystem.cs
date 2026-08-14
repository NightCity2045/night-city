// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.DeviceLinking.Systems;
using Content.Server.Medical.Components;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Diagnostics.Scanner;

/// <summary>
/// Maintains an event-driven link to the vanilla medical scanner. It never scans the world and never stores
/// the patient: the patient is read from the authoritative scanner container for every diagnostic request.
/// </summary>
public sealed partial class NCBodyScannerConsoleSystem : EntitySystem
{
    public static readonly ProtoId<SourcePortPrototype> ScannerPort = "MedicalScannerSender";

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
    /// Resolves the current occupant through the linked vanilla scanner. This is called for initial scans and
    /// every live refresh so ejecting or replacing a patient immediately invalidates the old observation.
    /// </summary>
    public bool TryGetPatient(
        Entity<NCBodyScannerConsoleComponent> console,
        out EntityUid patient,
        out NCDiagnosticAccessFailure failure)
    {
        patient = default;
        failure = NCDiagnosticAccessFailure.ScannerDisconnected;
        if (console.Comp.ConnectedScanner is not { Valid: true } scannerUid ||
            !TryComp<MedicalScannerComponent>(scannerUid, out var scanner))
        {
            return false;
        }

        failure = NCDiagnosticAccessFailure.ScannerEmpty;
        if (scanner.BodyContainer.ContainedEntity is not { Valid: true } occupant || !Exists(occupant))
            return false;

        patient = occupant;
        failure = NCDiagnosticAccessFailure.None;
        return true;
    }

    private void OnComponentInit(
        Entity<NCBodyScannerConsoleComponent> console,
        ref ComponentInit args)
    {
        _deviceLink.EnsureSourcePorts(console.Owner, ScannerPort);
    }

    private void OnMapInit(
        Entity<NCBodyScannerConsoleComponent> console,
        ref MapInitEvent args)
    {
        if (!TryComp<DeviceLinkSourceComponent>(console.Owner, out var source))
            return;

        var outputs = source.Outputs;
        // A source port is expected to have one body scanner. If a malformed map links several, choosing the
        // lowest UID keeps the authoritative result stable until the wiring is corrected.
        EntityUid? selected = null;
        if (outputs.TryGetValue(ScannerPort, out var sinks))
        {
            foreach (var sink in sinks)
            {
                if (!HasComp<MedicalScannerComponent>(sink) || selected is { } current && current.Id <= sink.Id)
                    continue;

                selected = sink;
            }
        }

        if (selected is { })
        {
            console.Comp.ConnectedScanner = selected;
            return;
        }

        // Maps predating this feature do not serialize a body-computer link. Establish a real device link to
        // the nearest vanilla scanner through one bounded spatial query; subsequent state stays event-driven.
        var origin = _transform.GetWorldPosition(console.Owner);
        var bestDistance = float.MaxValue;
        foreach (var scanner in _lookup.GetEntitiesInRange<MedicalScannerComponent>(
                     Transform(console.Owner).Coordinates,
                     source.Range))
        {
            var distance = (origin - _transform.GetWorldPosition(scanner.Owner)).LengthSquared();
            if (distance > bestDistance ||
                MathHelper.CloseTo(distance, bestDistance) && selected is { } current && current.Id <= scanner.Owner.Id)
            {
                continue;
            }

            selected = scanner.Owner;
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
            args.SourcePort == ScannerPort.Id &&
            HasComp<MedicalScannerComponent>(args.Sink))
        {
            console.Comp.ConnectedScanner = args.Sink;
        }
    }

    private void OnPortDisconnected(
        Entity<NCBodyScannerConsoleComponent> console,
        ref PortDisconnectedEvent args)
    {
        if (args.Port == ScannerPort.Id)
            console.Comp.ConnectedScanner = null;
    }
}
