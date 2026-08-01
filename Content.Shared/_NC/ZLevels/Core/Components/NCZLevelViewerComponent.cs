/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.ZLevels.Core.Components;

/// <summary>
/// Allows entity to see through Z-levels
/// </summary>
[UnsavedComponent]
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true)]
[Access(typeof(NCSharedZLevelsSystem))]
public sealed partial class NCZLevelViewerComponent : Component
{
    public HashSet<EntityUid> Eyes = new();

    /// <summary>
    /// We can look at 1 z-level up.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool LookUp;

    #region Actions

    [DataField]
    public EntProtoId ActionId = "NCActionToggleLookUp";

    [DataField, AutoNetworkedField]
    public EntityUid? ActionEntity;

    #endregion
}
