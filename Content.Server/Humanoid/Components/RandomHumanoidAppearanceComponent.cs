using Content.Shared.Humanoid; // NC

namespace Content.Server.Humanoid.Components;

[RegisterComponent]
public sealed partial class RandomHumanoidAppearanceComponent : Component
{
    [DataField]
    public bool RandomizeName = true;

    // NC-Edit-Start: Optional appearance restrictions for humanoid outfit prototypes.
    /// <summary>
    /// When set, only these cosmetic layers are kept from the randomized profile.
    /// Null preserves the normal species-wide randomization.
    /// </summary>
    [DataField]
    public HashSet<HumanoidVisualLayers>? AllowedMarkingLayers;
    // NC-Edit-End
}
