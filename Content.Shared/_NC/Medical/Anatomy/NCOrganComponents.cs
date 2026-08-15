// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// Structural state shared by biological organs, anatomical structures and cybernetic replacements.
/// </summary>
[RegisterComponent]
public sealed partial class NCOrganIntegrityComponent : Component
{
    [DataField]
    public float MaxIntegrity = 100f;

    [DataField]
    public float CurrentIntegrity = 100f;

    [DataField]
    public float FunctionalEfficiency = 1f;

    [DataField]
    public bool Recoverable = true;

    [DataField]
    public float Necrosis;

    [DataField]
    public NCAnatomicalMaterialKind Material = NCAnatomicalMaterialKind.Organic;
}

[RegisterComponent]
public sealed partial class NCOrganFunctionComponent : Component
{
    [DataField(required: true)]
    public NCOrganFunctionType FunctionType;

    [DataField]
    public float BaseCapacity = 1f;

    [DataField]
    public float CurrentCapacity = 1f;

    [DataField]
    public float ImpairedThreshold = 0.7f;

    [DataField]
    public float FailingThreshold = 0.35f;

    [DataField]
    public float NonFunctionalThreshold = 0.05f;

    [DataField]
    public bool Vital;

    [DataField]
    public bool Recoverable = true;

    [DataField]
    public float Necrosis;

    [DataField]
    public float PerfusionDemand;

    [DataField]
    public float OxygenDemand;
}

[RegisterComponent]
public sealed partial class NCBoneComponent : Component
{
    [DataField]
    public NCBoneState State = NCBoneState.Intact;

    [DataField]
    public float Stability = 1f;

    [DataField]
    public float Displacement;

    [DataField]
    public bool OpenFracture;

    [DataField]
    public float SecondaryDamageRisk;

    [DataField]
    public bool RequiresFixation;

    /// <summary>
    /// Field stabilization reduces movement loss and secondary injury but does not repair the fracture.
    /// </summary>
    [DataField]
    public bool Splinted;

    [DataField]
    public TimeSpan SplintedAt;
}

[RegisterComponent]
public sealed partial class NCTendonComponent : Component
{
    [DataField]
    public float Integrity = 1f;

    [DataField]
    public bool Ruptured;

    [DataField]
    public float MovementContribution = 1f;

    [DataField]
    public float GripContribution;

    [DataField]
    public bool SurgicallyRepairable = true;
}

[RegisterComponent]
public sealed partial class NCJointComponent : Component
{
    [DataField]
    public float Stability = 1f;

    [DataField]
    public bool Dislocated;

    [DataField]
    public float MovementRestriction;

    [DataField]
    public bool Reduced;
}

[RegisterComponent]
public sealed partial class NCVascularRegionComponent : Component
{
    [DataField]
    public float VascularDensity = 1f;

    [DataField]
    public bool HasMajorArtery;

    [DataField]
    public float ExternalBleedingCoefficient = 1f;

    [DataField]
    public NCBodyCavity Cavity;
}

[RegisterComponent]
public sealed partial class NCNerveComponent : Component
{
    [DataField]
    public float NeuralIntegrity = 1f;

    [DataField]
    public float Sensitivity = 1f;

    [DataField]
    public float MotorFunction = 1f;

    [DataField]
    public bool CyberprostheticInterface;
}

[RegisterComponent]
public sealed partial class NCSurgicalSiteComponent : Component
{
    [DataField]
    public NCSurgicalAccessDepth AccessDepth = NCSurgicalAccessDepth.Closed;

    [DataField]
    public bool BleedingControlled;

    [DataField]
    public bool BoneCasingOpen;

    [DataField]
    public bool TissuesRetracted;

    [DataField]
    public float Contamination;

    [DataField]
    public Guid? ActiveProcedure;

    [DataField]
    public HashSet<string> TemporaryDevices = [];
}

public enum NCAnatomicalMaterialKind : byte
{
    Organic,
    Mechanical,
    Hybrid,
}

public enum NCOrganFunctionType : byte
{
    Brain,
    Circulation,
    Respiration,
    Metabolism,
    Filtration,
    Digestion,
    Vision,
    Hearing,
    Speech,
    Movement,
    Grip,
}

public enum NCBoneState : byte
{
    Intact,
    Bruised,
    Cracked,
    BrokenStable,
    BrokenDisplaced,
    Shattered,
}

public enum NCBodyCavity : byte
{
    None,
    Cranial,
    Thoracic,
    Abdominal,
    Pelvic,
}
