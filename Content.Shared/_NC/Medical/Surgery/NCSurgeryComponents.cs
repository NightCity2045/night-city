// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Body;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// Server-only state machine attached to the materialized target region. The region itself is the stable
/// session handle, so no global registry or session-entity scan is required.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCSurgerySessionComponent : Component
{
    [DataField]
    public NCActionResolutionId ResolutionId;

    [DataField]
    public EntityUid Patient;

    [DataField]
    public EntityUid Surgeon;

    [DataField]
    public EntityUid TargetRegion;

    [DataField]
    public EntityUid? Facility;

    [DataField]
    public EntityUid? Environment;

    [DataField]
    public Guid? TargetWoundId;

    /// <summary>
    /// Optional detached live organ subtree selected as graft input. Sessions are unsaved, so this runtime
    /// EntityUid never enters persistence or the bounded step audit.
    /// </summary>
    [DataField]
    public EntityUid? Graft;

    /// <summary>
    /// Exact installed module selected by the server for a removal procedure. This session is unsaved and
    /// clients never submit or receive this entity identifier.
    /// </summary>
    [DataField]
    public EntityUid? CyberwareTarget;

    [ViewVariables]
    public bool CyberwareMutationApplied;

    /// <summary>
    /// Exact live subtree produced by a completed removal/amputation effect. Runtime-only handoff data.
    /// </summary>
    [ViewVariables]
    public EntityUid? DetachedResult;

    [DataField]
    public ProtoId<NCSurgeryProcedurePrototype> Procedure;

    [DataField]
    public int CurrentStepIndex;

    [DataField]
    public NCSurgerySessionState State;

    [DataField]
    public NCSurgeryInterruptionReason PauseReason;

    [DataField]
    public TimeSpan StartedAt;

    [DataField]
    public TimeSpan LastTransitionAt;

    [DataField]
    public NCActionResolutionId? TerminalResolutionId;

    /// <summary>
    /// At most one record is appended per procedure step. Rejected retries are not retained.
    /// </summary>
    [DataField]
    public List<NCSurgeryStepAuditRecord> History = [];
}

/// <summary>
/// Constant-time concurrency and authorization state for one surgeon.
/// </summary>
[RegisterComponent]
public sealed partial class NCSurgeryPractitionerComponent : Component
{
    [DataField]
    public HashSet<ProtoId<NCSurgeryAuthorizationPrototype>> Authorizations = [];

    [ViewVariables]
    public EntityUid? ActiveSession;
}

/// <summary>
/// Physical, observable capabilities of one tool instance.
/// </summary>
[RegisterComponent]
public sealed partial class NCSurgeryToolComponent : Component
{
    [DataField]
    public Dictionary<ProtoId<NCSurgeryToolCapabilityPrototype>, float> Capabilities = [];

    [DataField]
    public float Sterility = 1f;

    [DataField]
    public float Contamination;

    [DataField]
    public bool Broken;

    /// <summary>
    /// Negative is reusable; zero is exhausted; positive values are consumed by step prototypes.
    /// </summary>
    [DataField]
    public int UsesRemaining = -1;

    /// <summary>
    /// Optional real solution container consumed by surgical steps. This is used for vanilla syringes and
    /// avoids an invisible counter that can disagree with the reagent volume shown by the base game.
    /// </summary>
    [DataField]
    public string? SolutionResource;

    [DataField]
    public FixedPoint2 SolutionUnitsPerUse;

    /// <summary>
    /// Ordered, explicit procedures this tool may start when no UI selection exists. This avoids scanning
    /// the global procedure catalogue or guessing from tool names.
    /// </summary>
    [DataField]
    public List<ProtoId<NCSurgeryProcedurePrototype>> StartingProcedures = [];
}

/// <summary>
/// Physical authorization source observed while held. It represents clinic access scope, never skill.
/// </summary>
[RegisterComponent]
public sealed partial class NCSurgeryAuthorizationProviderComponent : Component
{
    [DataField]
    public HashSet<ProtoId<NCSurgeryAuthorizationPrototype>> Authorizations = [];
}

/// <summary>
/// Physical state of an explicitly selected surgical facility.
/// </summary>
[RegisterComponent]
public sealed partial class NCSurgeryFacilityComponent : Component
{
    [DataField]
    public HashSet<ProtoId<NCSurgeryFacilityCapabilityPrototype>> Capabilities = [];

    [DataField]
    public float Quality = 1f;

    [DataField]
    public float Sterility = 1f;

    [DataField]
    public bool Powered = true;

    [DataField]
    public bool StableSurface = true;
}

/// <summary>
/// Server-maintained room or field conditions bound explicitly to a surgery session.
/// </summary>
[RegisterComponent]
public sealed partial class NCSurgeryEnvironmentComponent : Component
{
    [DataField]
    public float Sterility;

    [DataField]
    public float Lighting = 1f;

    [DataField]
    public float Motion;

    [DataField]
    public float Contamination;
}

/// <summary>
/// Observable preparation supplied by a facility while the patient is actually buckled to it.
/// </summary>
[RegisterComponent]
public sealed partial class NCSurgeryPreparationProviderComponent : Component
{
    [DataField]
    public float Movement;

    [DataField]
    public bool Restrained;

    [DataField]
    public bool AirwayMonitored;
}

/// <summary>
/// Observable physical patient preparation supplied by the currently occupied facility. Drug effects are
/// intentionally absent: the surgery resolver reads them directly from the authoritative pharmacology system.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCSurgeryPatientStateComponent : Component
{
    [DataField]
    public EntityUid? CurrentFacility;

    [DataField]
    public EntityUid? CurrentEnvironment;

    [DataField]
    public float Movement;

    [DataField]
    public bool Restrained;

    [DataField]
    public bool AirwayMonitored;
}

/// <summary>
/// Marks a physical operating table or console that may open the private surgery planner. The server
/// resolves a patient only from an occupied surgical facility within this bounded radius.
/// </summary>
[RegisterComponent]
public sealed partial class NCSurgeryConsoleComponent : Component
{
    [DataField]
    public float FacilitySearchRange = 2.5f;
}

/// <summary>
/// Runtime graft tray on one physical surgical facility. A detached organ or standalone implant must be
/// placed through a real held-item interaction; clients cannot submit an entity identifier through the UI.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCSurgeryGraftSlotComponent : Component
{
    [ViewVariables]
    public EntityUid? Graft;

    [ViewVariables]
    public EntityUid? BoundBy;
}

/// <summary>
/// Persistence-safe pathological findings produced by deterministic regional autopsy procedures.
/// Records contain stable resolution/wound identifiers and prototype categories only.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCAutopsyRecordComponent : Component
{
    [DataField]
    public Dictionary<ProtoId<OrganCategoryPrototype>, NCAutopsyRegionRecord> Regions = new();
}
