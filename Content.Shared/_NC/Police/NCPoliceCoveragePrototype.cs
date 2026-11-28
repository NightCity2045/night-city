// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Police;

/// <summary>
/// How closely NCPD watches each district of the city atlas. <see cref="NCPoliceDistrictCoverage.Refresh"/> is how
/// often the monitoring console updates a person's position there; a district without one is not watched at all:
/// nobody shows on the console and gunfire raises no alert.
/// </summary>
[Prototype("ncPoliceCoverage")]
public sealed partial class NCPoliceCoveragePrototype : IPrototype
{
    public static readonly ProtoId<NCPoliceCoveragePrototype> DefaultId = "Default";

    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Refresh outside every district outline and in districts not listed. Null leaves them unwatched.</summary>
    [DataField]
    public TimeSpan? Outside = TimeSpan.FromSeconds(30);

    [DataField]
    public List<NCPoliceDistrictCoverage> Districts = [];
}

[DataDefinition]
public sealed partial class NCPoliceDistrictCoverage
{
    /// <summary>The district id in the city atlas.</summary>
    [DataField(required: true)]
    public string District = string.Empty;

    [DataField]
    public TimeSpan? Refresh;
}
