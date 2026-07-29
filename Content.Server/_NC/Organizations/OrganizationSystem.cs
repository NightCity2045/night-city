// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Shared._NC.Organizations;
using Robust.Shared.Prototypes;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server._NC.Organizations;

/// <summary>
/// Mirrors data-driven organization prototypes into durable database rows.
/// Prototype GUIDs are stable and must never be changed after production use.
/// </summary>
public sealed partial class OrganizationSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    private ISawmill _log = default!;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("nc.organizations");
        _ = SyncAsync();
    }

    private async Task SyncAsync()
    {
        try
        {
            var organizations = _prototypes.EnumeratePrototypes<NCOrganizationPrototype>()
                .Select(p => new NCOrganizationDefinition(
                    p.OrganizationId,
                    p.ID,
                    p.Name.Id,
                    p.DefaultEntryPosition == null
                        ? null
                        : _prototypes.Index(p.DefaultEntryPosition.Value).PositionId,
                    p.HasPayrollAccount,
                    p.PayrollStartingBalance,
                    p.CurrencyPrototypeId))
                .ToArray();
            var departments = _prototypes.EnumeratePrototypes<NCDepartmentPrototype>()
                .Select(p => new NCDepartmentDefinition(
                    p.DepartmentId,
                    _prototypes.Index(p.Organization).OrganizationId,
                    p.ID,
                    p.Name.Id))
                .ToArray();
            var positions = _prototypes.EnumeratePrototypes<NCPositionPrototype>()
                .Select(p => new NCPositionDefinition(
                    p.PositionId,
                    _prototypes.Index(p.Organization).OrganizationId,
                    p.Department == null ? null : _prototypes.Index(p.Department.Value).DepartmentId,
                    p.ID,
                    p.Name.Id,
                    p.RankWeight,
                    p.BaseSalary,
                    p.PayIntervalSeconds,
                    p.IsLeadership,
                    p.CanHire,
                    p.CanPromote,
                    p.CanDemote,
                    p.CanTransfer,
                    p.CanSuspend,
                    p.CanDismiss,
                    p.MaxPromotableRankWeight))
                .ToArray();

            await _database.SyncNCOrganizationsAsync(organizations, departments, positions);
        }
        catch (Exception exception)
        {
            _log.Error($"Failed to synchronize organization prototypes: {exception}");
        }
    }
}
