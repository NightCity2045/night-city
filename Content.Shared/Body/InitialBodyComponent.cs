using Robust.Shared.Prototypes;

namespace Content.Shared.Body;

/// <summary>
/// On map initialization, spawns the given organs into the body.
/// Liable to change as the body becomes more complex.
/// </summary>
[RegisterComponent]
[Access(typeof(InitialBodySystem))]
public sealed partial class InitialBodyComponent : Component
{
    /// <summary>
    /// The organs to spawn based on their category.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<OrganCategoryPrototype>, EntProtoId<OrganComponent>> Organs;

    // NC-Edit-Start
    /// <summary>Expected graph nodes deliberately absent from a newly manufactured empty body.</summary>
    [DataField]
    public HashSet<ProtoId<OrganCategoryPrototype>> ExcludedOrgans = [];
    // NC-Edit-End

    /// <summary>
    /// Organ relationships, based on their categories. Keys are parents to their values.
    /// </summary>
    [DataField]
    public Dictionary<ProtoId<OrganCategoryPrototype>, HashSet<ProtoId<OrganCategoryPrototype>>>? Relationships;
}
