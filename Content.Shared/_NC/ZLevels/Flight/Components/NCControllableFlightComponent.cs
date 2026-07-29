/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.ZLevels.Flight.Components;

/// <summary>
/// Allows an entity to control its own flight status
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true),
 Access(typeof(NCSharedZFlightSystem))]
public sealed partial class NCControllableFlightComponent : Component
{
    [DataField]
    public EntProtoId UpActionProto = "NCActionZFlightUp";

    [DataField, AutoNetworkedField]
    public EntityUid? ZLevelUpActionEntity;

    [DataField]
    public EntProtoId DownActionProto = "NCActionZFlightDown";

    [DataField, AutoNetworkedField]
    public EntityUid? ZLevelDownActionEntity;

    [DataField]
    public EntProtoId ToggleActionProto = "NCActionZFlightToggle";

    [DataField, AutoNetworkedField]
    public EntityUid? ZLevelToggleActionEntity;

    [DataField]
    public TimeSpan? StartFlightDoAfter;
}
