// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Shared._NC.City.Zones.Serialization;

/// <summary>Field-only root-parent sentinel support; never register this as the default NCZoneId serializer.</summary>
public sealed class NCZoneParentIdSerializer : NCCityObjectIdSerializer<NCZoneId>
{
    protected override string TypeName => nameof(NCZoneId);

    protected override bool TryParse(string value, out NCZoneId id)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            id = default;
            return false;
        }
        id = new(guid);
        return true;
    }
}
