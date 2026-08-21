// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Ambient;

[Serializable, NetSerializable]
public enum NCAmbientCrowdKind : byte
{
    Decorative,
    Interactive,
}

/// <summary>
/// One data-driven ambient population class. Decorative profiles are rendered client-side; interactive profiles
/// point at a physical entity prototype but never create a persistent actor identity.
/// </summary>
[Prototype("ncAmbientCrowdProfile")]
public sealed partial class NCAmbientCrowdProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCAmbientCrowdKind Kind { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCCitySpawnCandidateProfilePrototype> SpawnCandidateProfile { get; private set; }

    [DataField]
    public EntProtoId? EntityPrototype { get; private set; }

    [DataField]
    public int Weight { get; private set; } = 1;

    [DataField]
    public TimeSpan MinimumLifetime { get; private set; } = TimeSpan.FromMinutes(2);

    [DataField]
    public TimeSpan MaximumLifetime { get; private set; } = TimeSpan.FromMinutes(5);

    [DataField]
    public TimeSpan MinimumDecisionInterval { get; private set; } = TimeSpan.FromSeconds(5);

    [DataField]
    public TimeSpan MaximumDecisionInterval { get; private set; } = TimeSpan.FromSeconds(12);

    [DataField]
    public bool InteractWithDoors { get; private set; } = true;

    [DataField]
    public Color SilhouetteColor { get; private set; } = new(84, 108, 116, 150);

    [DataField]
    public float SilhouetteScale { get; private set; } = 1f;

    [DataField]
    public float SilhouetteMotionRadius { get; private set; } = 0.35f;

    [DataField]
    public float SilhouetteMotionSpeed { get; private set; } = 0.6f;
}

/// <summary>
/// Bounded policy for both ambient layers. Every value is authored in YAML so density can be changed without code.
/// </summary>
[Prototype("ncAmbientCrowdPolicy")]
public sealed partial class NCAmbientCrowdPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<ProtoId<NCAmbientCrowdProfilePrototype>> Profiles { get; private set; } = [];

    [DataField]
    public TimeSpan EvaluationInterval { get; private set; } = TimeSpan.FromSeconds(2);

    [DataField]
    public int DecorativePerCluster { get; private set; } = 8;

    [DataField]
    public int DecorativePerPlayer { get; private set; } = 4;

    [DataField]
    public int MaximumDecorativePerClient { get; private set; } = 48;

    [DataField]
    public int InteractivePerCluster { get; private set; } = 2;

    [DataField]
    public int InteractivePerPlayer { get; private set; } = 1;

    [DataField]
    public int MaximumInteractivePerCluster { get; private set; } = 8;

    [DataField]
    public int MaximumInteractiveGlobal { get; private set; } = 48;

    [DataField]
    public float ElevatedDensityMultiplier { get; private set; } = 0.5f;

    [DataField]
    public float CriticalDensityMultiplier { get; private set; }

    [DataField]
    public int MaximumSpawnsPerEvaluation { get; private set; } = 3;

    [DataField]
    public int MaximumDespawnsPerEvaluation { get; private set; } = 6;

    [DataField]
    public int MaximumCandidateChecks { get; private set; } = 32;

    [DataField]
    public float SpawnVisibilityExclusionRadius { get; private set; } = 7f;

    [DataField]
    public float DespawnVisibilityExclusionRadius { get; private set; } = 9f;

    [DataField]
    public float OccupancyExclusionRadius { get; private set; } = 0.75f;

    [DataField]
    public float ClusterRetentionRadius { get; private set; } = 34f;

    [DataField]
    public float MovementReachRange { get; private set; } = 0.55f;

    [DataField]
    public int MaximumWitnessQueryResults { get; private set; } = 32;
}
