// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Text;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Makes the stable property set of an unknown compound. The same world seed and the same kind of raw material
/// always give the same compound, so it can be rebuilt after a restart without storing its numbers: the
/// properties are a fact of the world, not something rolled again on every use.
/// </summary>
public static class NCCompoundGenerator
{
    private readonly record struct Archetype(string Category, string Field, float Low, float High);

    private static readonly Archetype[] Actions =
    [
        new("Analgesic", "analgesiaAtTherapeutic", 0.3f, 0.8f),
        new("Sedative", "sedationAtTherapeutic", 0.3f, 0.7f),
        new("Stimulant", "arousalAtTherapeutic", 0.3f, 0.7f),
        new("Antibiotic", "antibioticPowerAtTherapeutic", 0.5f, 0.95f),
        new("Immunosuppressant", "immunosuppressionAtTherapeutic", 0.3f, 0.8f),
        new("Antitoxin", "toxinRecoveryPerSecondAtTherapeutic", 0.5f, 1.5f),
    ];

    private static readonly Archetype[] SideEffects =
    [
        new("", "rhythmInstabilityAtTherapeutic", 0.03f, 0.12f),
        new("", "motorControlMultiplierAtTherapeutic", 0.82f, 0.95f),
        new("", "respiratoryTransferMultiplierAtTherapeutic", 0.85f, 0.95f),
        new("", "cardiacOutputMultiplierAtTherapeutic", 1.08f, 1.25f),
    ];

    private static readonly string[] Dependencies = ["NCDependenceSocial", "NCDependenceEuphoric", "NCDependenceX17"];

    /// <summary>Prototype id of the compound a material turns out to be.</summary>
    public static string IdFor(uint seed, string key) => "NCDrugGen" + Hash(seed, key).ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The short code that names it until it is identified.</summary>
    public static string CodeFor(uint seed, string key) => "G" + Hash(seed, key).ToString("X8", CultureInfo.InvariantCulture)[4..];

    /// <summary>Builds the prototype text of the compound. <paramref name="pool"/> sets how potent and how risky.</summary>
    public static string ToYaml(uint seed, string key, string pool)
    {
        var rng = new Rng(Hash(seed, key) ^ 0x9E3779B9u);
        var corporate = pool == "corporate";
        var action = Actions[rng.Next(Actions.Length)];
        var potency = corporate ? rng.Range(0.6f, 1f) : rng.Range(0f, 1f);
        var primary = Round(action.Low + (action.High - action.Low) * potency, 2);

        var therapeutic = Round(rng.Range(2f, 8f), 1);
        var toxic = Round(therapeutic * (corporate ? rng.Range(2f, 5f) : rng.Range(1.4f, 4f)), 1);
        var maximum = Round(toxic * rng.Range(2.2f, 4f), 1);
        var clearance = Round(rng.Range(0.015f, 0.07f), 3);
        var liver = Round(rng.Range(0.2f, 0.7f), 2);
        var kidney = Round(MathF.Min(0.95f - liver, rng.Range(0.1f, 0.5f)), 2);
        var baseline = Round(MathF.Max(0f, MathF.Min(0.2f, 1f - liver - kidney - 0.02f)), 2);
        var overdose = Round(rng.Range(0.6f, 1.4f), 1);

        var builder = new StringBuilder();
        builder.AppendLine("- type: ncDrugProfile");
        builder.AppendLine($"  id: {IdFor(seed, key)}");
        builder.AppendLine($"  category: {action.Category}");
        builder.AppendLine("  codename: nc-lab-codename-generated");
        builder.AppendLine($"  codenameCode: {CodeFor(seed, key)}");
        builder.AppendLine("  interactionsCharted: false");
        if (rng.Chance(corporate ? 0.2f : 0.55f))
            builder.AppendLine($"  dependence: {Dependencies[rng.Next(Dependencies.Length)]}");

        builder.AppendLine($"  maximumConcentration: {Format(maximum)}");
        builder.AppendLine("  removalConcentration: 0.05");
        builder.AppendLine("  minimumEffectiveConcentration: 1");
        builder.AppendLine($"  therapeuticConcentration: {Format(therapeutic)}");
        builder.AppendLine($"  toxicConcentration: {Format(toxic)}");
        builder.AppendLine($"  clearancePerSecond: {Format(clearance)}");
        builder.AppendLine($"  baselineClearanceFraction: {Format(baseline)}");
        builder.AppendLine($"  liverClearanceFraction: {Format(liver)}");
        builder.AppendLine($"  kidneyClearanceFraction: {Format(kidney)}");
        builder.AppendLine($"  {action.Field}: {Format(primary)}");
        if (rng.Chance(corporate ? 0.35f : 0.65f))
        {
            var side = SideEffects[rng.Next(SideEffects.Length)];
            builder.AppendLine($"  {side.Field}: {Format(Round(rng.Range(side.Low, side.High), 2))}");
        }

        builder.AppendLine($"  overdoseBurdenPerSecondAtMaximum: {Format(overdose)}");
        return builder.ToString();
    }

    private static uint Hash(uint seed, string key) => NCLabSeed.Hash(seed.ToString(CultureInfo.InvariantCulture) + ":" + key);

    private static float Round(float value, int digits) => MathF.Round(value, digits);

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Small deterministic generator (xorshift). Never used for anything that must be unpredictable.</summary>
    private struct Rng(uint seed)
    {
        private uint _state = seed == 0 ? 2463534242u : seed;

        public uint NextUInt()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        public int Next(int exclusiveMax) => (int) (NextUInt() % (uint) exclusiveMax);

        public float Range(float low, float high) => low + (high - low) * (NextUInt() % 10000u / 10000f);

        public bool Chance(float probability) => NextUInt() % 10000u / 10000f < probability;
    }
}
