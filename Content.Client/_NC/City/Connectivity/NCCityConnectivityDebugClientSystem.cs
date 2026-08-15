// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Console;

namespace Content.Client._NC.City.Connectivity;

/// <summary>
/// Owns the latest authorized diagnostic snapshot and its overlay lifecycle.
/// </summary>
public sealed partial class NCCityConnectivityDebugClientSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;

    public NCCityConnectivityDebugSnapshot? Snapshot { get; private set; }
    public bool Enabled => Snapshot?.Enabled == true;
    public IReadOnlySet<NCZoneId> ErrorZones => _errorZones;
    public IReadOnlySet<NCZoneId> WarningZones => _warningZones;

    private readonly HashSet<NCZoneId> _errorZones = [];
    private readonly HashSet<NCZoneId> _warningZones = [];
    private NCCityConnectivityDebugOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCCityConnectivityDebugSnapshot>(OnSnapshot);
    }

    public override void Shutdown()
    {
        RemoveOverlay();
        Snapshot = null;
        _errorZones.Clear();
        _warningZones.Clear();
        base.Shutdown();
    }

    public void SetEnabled(bool enabled)
    {
        RaiseNetworkEvent(new NCCityConnectivityDebugRequest(enabled));
    }

    public bool TryGetViewCoordinates(out NCMapCoordinates coordinates)
    {
        if (_players.LocalEntity is { } entity &&
            _coordinates.TryGetCoordinates(entity, out coordinates))
        {
            return true;
        }

        coordinates = default;
        return false;
    }

    private void OnSnapshot(NCCityConnectivityDebugSnapshot snapshot)
    {
        Snapshot = snapshot;
        _errorZones.Clear();
        _warningZones.Clear();
        foreach (var issue in snapshot.Issues)
        {
            if (issue.Severity == NCConnectivityDebugIssueSeverity.Error)
                _errorZones.Add(issue.ZoneId);
            else
                _warningZones.Add(issue.ZoneId);
        }

        if (!snapshot.Enabled || !snapshot.Authorized)
        {
            RemoveOverlay();
            return;
        }

        if (_overlay != null)
            return;
        _overlay = new NCCityConnectivityDebugOverlay(this);
        _overlays.AddOverlay(_overlay);
    }

    private void RemoveOverlay()
    {
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);
        _overlay = null;
    }
}

public sealed partial class NCShowCityConnectivityDebugCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entities = default!;

    public override string Command => "nccitygraph-overlay";
    public override string Description => "Toggle the authorized Night City connectivity overlay.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var system = _entities.System<NCCityConnectivityDebugClientSystem>();
        system.SetEnabled(!system.Enabled);
        shell.WriteLine(system.Enabled
            ? "Connectivity overlay disable requested."
            : "Connectivity overlay enable requested; Mapping permission is required.");
    }
}
