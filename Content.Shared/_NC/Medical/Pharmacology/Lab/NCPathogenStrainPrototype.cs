// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>
/// A bacterial strain and how well each antibiotic works on it. The sensitivity profile is the whole point of
/// a culture: a narrow antibiotic that matches the strain beats a broad one, and a mismatch barely helps.
/// </summary>
[Prototype("ncPathogenStrain")]
public sealed partial class NCPathogenStrainPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    /// <summary>Share of an antibiotic's power that works on this strain. A missing entry means full power.</summary>
    [DataField]
    public Dictionary<ProtoId<NCDrugProfilePrototype>, float> Susceptibility { get; private set; } = new();

    /// <summary>Relative chance that a body carries this strain.</summary>
    [DataField]
    public float Weight { get; private set; } = 1f;
}

/// <summary>Deterministic choices shared by the server systems that need a stable per-character trait.</summary>
public static class NCLabSeed
{
    /// <summary>FNV-1a of a name, so the same character always gets the same result without stored state.</summary>
    public static uint Hash(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    /// <summary>Picks one key by weight. Keys are ordered by the caller so the pick is reproducible.</summary>
    public static T? Pick<T>(IReadOnlyList<(T Key, float Weight)> entries, uint seed)
    {
        var total = 0f;
        foreach (var (_, weight) in entries)
            total += MathF.Max(0f, weight);

        if (total <= 0f)
            return default;

        var target = seed % 10000u / 10000f * total;
        var running = 0f;
        foreach (var (key, weight) in entries)
        {
            running += MathF.Max(0f, weight);
            if (target < running)
                return key;
        }

        return entries[^1].Key;
    }
}
