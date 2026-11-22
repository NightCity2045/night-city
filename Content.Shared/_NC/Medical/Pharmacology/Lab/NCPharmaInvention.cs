// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using System.Text;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>What one ingredient contributes to an invented compound.</summary>
public readonly record struct NCIngredientTraits(
    string Reagent,
    string Category,
    float Potency,
    float Toxicity,
    string Side,
    string? Dependence);

/// <summary>The numbers of an invented compound, before they become a drug profile.</summary>
public readonly record struct NCInventedProfile(
    string Category,
    float Strength,
    float Primary,
    float Therapeutic,
    float Toxic,
    float Maximum,
    float Clearance,
    float Overdose,
    string? SideField,
    float SideValue,
    string? Dependence);

/// <summary>
/// Turns a set of ingredients into a compound. There is no randomness: the same ingredients always make the same
/// compound, in every world. What the ingredients lean towards decides what the compound does, how many of them
/// pull the same way decides how strong it is, and whatever pulls against it, or is simply harmful, makes it
/// dangerous. A neutraliser takes harm away.
/// </summary>
public static class NCPharmaInvention
{
    public const int MinimumIngredients = 2;
    public const int MaximumIngredients = 3;

    private readonly record struct Action(string Field, float Low, float High);

    private static readonly Dictionary<string, Action> Actions = new()
    {
        ["Analgesic"] = new("analgesiaAtTherapeutic", 0.3f, 0.8f),
        ["Sedative"] = new("sedationAtTherapeutic", 0.3f, 0.7f),
        ["Stimulant"] = new("arousalAtTherapeutic", 0.3f, 0.7f),
        ["Antibiotic"] = new("antibioticPowerAtTherapeutic", 0.5f, 0.95f),
        ["Immunosuppressant"] = new("immunosuppressionAtTherapeutic", 0.3f, 0.8f),
        ["Antitoxin"] = new("toxinRecoveryPerSecondAtTherapeutic", 0.5f, 1.5f),
    };

    private static readonly Dictionary<string, (string Field, float Low, float High)> Sides = new()
    {
        ["rhythm"] = ("rhythmInstabilityAtTherapeutic", 0.03f, 0.12f),
        ["motor"] = ("motorControlMultiplierAtTherapeutic", 0.95f, 0.82f),
        ["respiratory"] = ("respiratoryTransferMultiplierAtTherapeutic", 0.95f, 0.85f),
        ["cardiac"] = ("cardiacOutputMultiplierAtTherapeutic", 1.08f, 1.25f),
    };

    public static bool IsCategory(string category) => Actions.ContainsKey(category);

    /// <summary>The key of a recipe: the ingredient reagents in a fixed order, so the order of choosing never matters.</summary>
    public static string KeyOf(IEnumerable<string> reagents) =>
        string.Join("+", reagents.OrderBy(reagent => reagent, StringComparer.Ordinal));

    public static string IdFor(string key) => "NCDrugInv" + NCLabSeed.Hash("invent:" + key).ToString("X8", CultureInfo.InvariantCulture);

    public static string CodeFor(string key) => "I" + NCLabSeed.Hash("invent:" + key).ToString("X8", CultureInfo.InvariantCulture)[4..];

    /// <summary>Combines the ingredients, or says no when they cannot make a compound that does anything.</summary>
    public static bool TryCombine(IReadOnlyList<NCIngredientTraits> ingredients, out NCInventedProfile profile)
    {
        profile = default;
        if (ingredients.Count is < MinimumIngredients or > MaximumIngredients)
            return false;

        var scores = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var ingredient in ingredients)
        {
            if (ingredient.Category.Length == 0 || !Actions.ContainsKey(ingredient.Category))
                continue;
            scores[ingredient.Category] = scores.GetValueOrDefault(ingredient.Category) + ingredient.Potency;
        }

        if (scores.Count == 0)
            return false;

        var category = scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
        var action = Actions[category.Key];
        var pulling = category.Value;
        var against = scores.Where(pair => pair.Key != category.Key).Sum(pair => pair.Value);
        var strength = Math.Clamp(pulling / 1.4f, 0.1f, 1f);

        var harm = 0f;
        foreach (var ingredient in ingredients)
            harm += ingredient.Toxicity;
        harm = harm / ingredients.Count + against * 0.3f;
        harm = Math.Clamp(harm, 0.05f, 1f);

        var therapeutic = Round(Lerp(7f, 3f, strength), 1);
        var toxic = Round(therapeutic * Lerp(4.5f, 1.4f, harm), 1);
        var maximum = Round(toxic * 3f, 1);
        var clearance = Round(0.06f - 0.015f * (ingredients.Count - 1), 3);
        var overdose = Round(0.6f + harm * 0.8f, 1);

        string? sideField = null;
        var sideValue = 0f;
        foreach (var ingredient in ingredients.OrderBy(ingredient => ingredient.Reagent, StringComparer.Ordinal))
        {
            if (ingredient.Side.Length == 0 || !Sides.TryGetValue(ingredient.Side, out var side))
                continue;
            sideField = side.Field;
            sideValue = Round(Lerp(side.Low, side.High, harm), 2);
            break;
        }

        var dependence = ingredients
            .OrderBy(ingredient => ingredient.Reagent, StringComparer.Ordinal)
            .Select(ingredient => ingredient.Dependence)
            .FirstOrDefault(found => !string.IsNullOrEmpty(found));

        profile = new NCInventedProfile(
            category.Key,
            strength,
            Round(Lerp(action.Low, action.High, strength), 2),
            therapeutic,
            toxic,
            maximum,
            clearance,
            overdose,
            sideField,
            sideValue,
            dependence);
        return true;
    }

    /// <summary>The prototype text of the compound, in the shape every drug profile has.</summary>
    public static string ToYaml(string key, NCInventedProfile profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine("- type: ncDrugProfile");
        builder.AppendLine($"  id: {IdFor(key)}");
        builder.AppendLine($"  category: {profile.Category}");
        builder.AppendLine($"  feedstock: {NCCompoundGenerator.FeedstockFor(profile.Category)}");
        builder.AppendLine("  codename: nc-lab-codename-invented");
        builder.AppendLine($"  codenameCode: {CodeFor(key)}");
        builder.AppendLine("  interactionsCharted: false");
        if (profile.Dependence != null)
            builder.AppendLine($"  dependence: {profile.Dependence}");

        builder.AppendLine($"  maximumConcentration: {Format(profile.Maximum)}");
        builder.AppendLine("  removalConcentration: 0.05");
        builder.AppendLine("  minimumEffectiveConcentration: 1");
        builder.AppendLine($"  therapeuticConcentration: {Format(profile.Therapeutic)}");
        builder.AppendLine($"  toxicConcentration: {Format(profile.Toxic)}");
        builder.AppendLine($"  clearancePerSecond: {Format(profile.Clearance)}");
        builder.AppendLine("  baselineClearanceFraction: 0.1");
        builder.AppendLine("  liverClearanceFraction: 0.5");
        builder.AppendLine("  kidneyClearanceFraction: 0.3");
        builder.AppendLine($"  {Actions[profile.Category].Field}: {Format(profile.Primary)}");
        if (profile.SideField != null)
            builder.AppendLine($"  {profile.SideField}: {Format(profile.SideValue)}");
        builder.AppendLine($"  overdoseBurdenPerSecondAtMaximum: {Format(profile.Overdose)}");
        return builder.ToString();
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    private static float Round(float value, int digits) => MathF.Round(value, digits);

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
