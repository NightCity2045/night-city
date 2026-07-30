/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Serialization;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._NC.ZLevels.Mapping.Prototypes;

[Prototype("zMap")]
public sealed partial class NCZLevelMapPrototype : IPrototype
{
    /// <inheritdoc/>
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Stable identity used when this prototype is loaded as a persistent Z-network.
    /// </summary>
    [DataField(required: true, customTypeSerializer: typeof(NCZNetworkIdSerializer))]
    public NCZNetworkId NetworkId { get; private set; }

    [DataField]
    public List<ResPath> Maps = new();

    /// <summary>
    /// Shared components for all zLevels maps
    /// </summary>
    [DataField]
    public ComponentRegistry Components = new();
}
