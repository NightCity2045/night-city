// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using NUnit.Framework;

namespace Content.Tests._NC.Medical.Pharmacology;

[TestFixture]
public sealed class NCCompoundDossierTests
{
    private static NCDrugProfilePrototype Drug(params (string Name, object Value)[] values)
    {
        var drug = new NCDrugProfilePrototype();
        foreach (var (name, value) in values)
        {
            typeof(NCDrugProfilePrototype)
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(drug, value);
        }

        return drug;
    }

    private static NCDrugProfilePrototype Stimulant() => Drug(
        (nameof(NCDrugProfilePrototype.ArousalAtTherapeutic), 0.6f),
        (nameof(NCDrugProfilePrototype.CardiacOutputMultiplierAtTherapeutic), 1.2f),
        (nameof(NCDrugProfilePrototype.ClearancePerSecond), 0.01f),
        (nameof(NCDrugProfilePrototype.LiverClearanceFraction), 0.7f),
        (nameof(NCDrugProfilePrototype.TherapeuticConcentration), 4f),
        (nameof(NCDrugProfilePrototype.ToxicConcentration), 6f));

    [Test]
    public void DossierIsDerivedFromTheRealProfile()
    {
        var properties = NCCompoundDossierMath.Build(Stimulant(), null);
        Assert.That(properties.Single(p => p.Kind == NCCompoundPropertyKind.PrimaryAction).Value, Is.EqualTo("arousal"));
        Assert.That(properties.Single(p => p.Kind == NCCompoundPropertyKind.SecondaryAction).Value, Is.EqualTo("pressure-up"));
        Assert.That(properties.Single(p => p.Kind == NCCompoundPropertyKind.MetabolicRoute).Value, Is.EqualTo("liver"));
        Assert.That(properties.Single(p => p.Kind == NCCompoundPropertyKind.Duration).Value, Is.EqualTo("moderate"));
        Assert.That(properties.Single(p => p.Kind == NCCompoundPropertyKind.TherapeuticWindow).Value, Is.EqualTo("narrow"));
    }

    [Test]
    public void BuildingTwiceGivesTheSameDossier()
    {
        Assert.That(NCCompoundDossierMath.Build(Stimulant(), null), Is.EqualTo(NCCompoundDossierMath.Build(Stimulant(), null)));
    }

    [Test]
    public void KnowledgeGrowsWithDeeperMethods()
    {
        var properties = NCCompoundDossierMath.Build(Stimulant(), null);

        var none = NCCompoundDossierMath.Read(properties, NCAnalysisTier.None);
        Assert.That(none.All(r => r.State == NCCompoundPropertyState.Unknown));

        var chemical = NCCompoundDossierMath.Read(properties, NCCompoundDossierMath.Include(NCAnalysisTier.None, NCAnalysisTier.Chemical));
        Assert.That(chemical.Single(r => r.Kind == NCCompoundPropertyKind.MetabolicRoute).State, Is.EqualTo(NCCompoundPropertyState.Confirmed));
        Assert.That(chemical.Single(r => r.Kind == NCCompoundPropertyKind.PrimaryAction).State, Is.EqualTo(NCCompoundPropertyState.Unknown));

        var vitro = NCCompoundDossierMath.Read(properties, NCCompoundDossierMath.Include(NCAnalysisTier.Chemical, NCAnalysisTier.InVitro));
        Assert.That(vitro.Single(r => r.Kind == NCCompoundPropertyKind.PrimaryAction).State, Is.EqualTo(NCCompoundPropertyState.Confirmed));
        Assert.That(vitro.Single(r => r.Kind == NCCompoundPropertyKind.SecondaryAction).State, Is.EqualTo(NCCompoundPropertyState.Suspected));
        Assert.That(vitro.Single(r => r.Kind == NCCompoundPropertyKind.Duration).State, Is.EqualTo(NCCompoundPropertyState.Unknown));

        var vivo = NCCompoundDossierMath.Read(properties, NCCompoundDossierMath.Include(NCAnalysisTier.None, NCAnalysisTier.InVivo));
        Assert.That(vivo.All(r => r.State == NCCompoundPropertyState.Confirmed));
    }

    [Test]
    public void SeedPickIsStableAndFollowsWeights()
    {
        var entries = new List<(string, float)> { ("a", 1f), ("b", 0f), ("c", 3f) };
        Assert.That(NCLabSeed.Pick(entries, NCLabSeed.Hash("Alex Mercer")), Is.EqualTo(NCLabSeed.Pick(entries, NCLabSeed.Hash("Alex Mercer"))));
        for (var seed = 0u; seed < 500u; seed++)
            Assert.That(NCLabSeed.Pick(entries, seed), Is.Not.EqualTo("b"));
        Assert.That(NCLabSeed.Pick(new List<(string, float)>(), 5u), Is.Null);
    }

    [Test]
    public void GeneratedCompoundsAreStableDistinctAndSane()
    {
        Assert.That(NCCompoundGenerator.IdFor(7u, "raw-a"), Is.EqualTo(NCCompoundGenerator.IdFor(7u, "raw-a")));
        Assert.That(NCCompoundGenerator.IdFor(7u, "raw-a"), Is.Not.EqualTo(NCCompoundGenerator.IdFor(7u, "raw-b")));
        Assert.That(NCCompoundGenerator.IdFor(7u, "raw-a"), Is.Not.EqualTo(NCCompoundGenerator.IdFor(8u, "raw-a")));
        Assert.That(NCCompoundGenerator.ToYaml(7u, "raw-a", "street"), Is.EqualTo(NCCompoundGenerator.ToYaml(7u, "raw-a", "street")));
        for (var seed = 1u; seed < 200u; seed++)
        {
            var yaml = NCCompoundGenerator.ToYaml(seed, "raw", seed % 2 == 0 ? "street" : "corporate");
            var values = yaml.Split('\n')
                .Select(line => line.Trim().Split(": "))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);
            float V(string key) => float.Parse(values[key], System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(V("therapeuticConcentration"), Is.LessThan(V("toxicConcentration")));
            Assert.That(V("toxicConcentration"), Is.LessThan(V("maximumConcentration")));
            Assert.That(V("baselineClearanceFraction") + V("liverClearanceFraction") + V("kidneyClearanceFraction"), Is.LessThanOrEqualTo(1f));
        }
    }

    [Test]
    public void ProcedureQualityFollowsTheActorsBodyAndNeverBelowTheFloor()
    {
        var procedure = new NCBiotechProcedurePrototype();
        void Set(string name, float value) => typeof(NCBiotechProcedurePrototype)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(procedure, value);
        Set(nameof(NCBiotechProcedurePrototype.GripDemand), 0.5f);
        Set(nameof(NCBiotechProcedurePrototype.SteadinessDemand), 0.5f);

        Assert.That(NCBiotechQualityMath.Resolve(procedure, new NCBiotechActorState(1f, 1f, 1f, 1f)), Is.EqualTo(1f));
        var shaky = NCBiotechQualityMath.Resolve(procedure, new NCBiotechActorState(1f, 1f, 0.4f, 1f));
        var crippled = NCBiotechQualityMath.Resolve(procedure, new NCBiotechActorState(0.2f, 1f, 0.4f, 1f));
        Assert.That(shaky, Is.LessThan(1f));
        Assert.That(crippled, Is.LessThan(shaky));
        Assert.That(NCBiotechQualityMath.Resolve(procedure, new NCBiotechActorState(0f, 0f, 0f, 0f)), Is.EqualTo(NCBiotechQualityMath.Floor));
        Assert.That(NCBiotechQualityMath.Resolve(procedure, new NCBiotechActorState(1f, 1f, float.NaN, 1f)), Is.EqualTo(1f));
    }
}
