/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Shared.GameStates;

namespace Content.Shared._NC.ZLevels.Damage.FallingImmunity;

/// <summary>
/// Data-driven multipliers applied to fall damage while this status effect is active.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCFallingImmunityStatusEffectComponent : Component
{
    [DataField, AutoNetworkedField]
    public float DamageMultiplier = 1f;

    [DataField, AutoNetworkedField]
    public float StunMultiplier = 1f;
}
