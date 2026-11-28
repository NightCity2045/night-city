// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server._NC.City.Atlas;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Police;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Police;

/// <summary>
/// Answers how closely NCPD watches a place: how often the monitoring console refreshes a person there, and whether
/// gunfire there raises an alert. Districts come from the city atlas; coverage from <see cref="NCPoliceCoveragePrototype"/>.
/// </summary>
public sealed partial class NCPoliceCoverageSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;

    /// <summary>
    /// The refresh interval of the place, or false when NCPD does not watch it.
    /// </summary>
    public bool TryGetRefresh(NCMapCoordinates coordinates, out TimeSpan refresh)
    {
        refresh = default;
        if (!_prototypes.TryIndex(NCPoliceCoveragePrototype.DefaultId, out var coverage))
            return false;

        TimeSpan? found = coverage.Outside;
        if (_atlas.TryGetDistrict(coordinates, out var district))
        {
            // A district not listed is watched like the streets outside every district.
            foreach (var entry in coverage.Districts)
            {
                if (entry.District != district.Id)
                    continue;

                found = entry.Refresh;
                break;
            }
        }

        if (found is not { } value)
            return false;

        refresh = value;
        return true;
    }

    public bool IsCovered(NCMapCoordinates coordinates)
    {
        return TryGetRefresh(coordinates, out _);
    }
}
