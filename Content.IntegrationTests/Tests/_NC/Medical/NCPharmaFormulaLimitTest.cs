// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// The laboratory's cap on separate formula lines. It fills the laboratory, so the server is thrown away after it.
/// Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaFormulaLimitTest : GameTest
{
    private const string Autoinjector = "NCPharmaDeliveryAutoinjector";
    private const string Standard = "NCPharmaProductionStandard";

    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new() { Connected = false, Fresh = true, Dirty = true };

    [Test]
    [RunOnSide(Side.Server)]
    public void ALaboratoryCannotDevelopMoreLinesThanItsLimit()
    {
        var cores = _batches.AvailableCores().Select(core => core.Id).ToList();
        var limit = _prototypes.EnumeratePrototypes<NCPharmaTuningPrototype>().First(tuning => tuning.DefaultTuning).MaximumLineages;
        var made = 0;
        var refused = false;
        for (var i = 0; i < cores.Count && !refused; i++)
        {
            for (var j = i + 1; j < cores.Count && !refused; j++)
            {
                var concentration = _prototypes.Index<NCDrugProfilePrototype>(cores[i]).TherapeuticConcentration;
                if (_batches.TryComposeFormula(null, "Tester", cores[i], cores[j], Autoinjector, concentration, [], Standard,
                        out _, out var notice))
                {
                    made++;
                }
                else if (notice == NCPharmaRdNotice.TooManyFormulas)
                {
                    refused = true;
                }
            }
        }

        Assert.That(refused, Is.True, "the cap must be reached");
        Assert.That(made, Is.EqualTo(limit));
    }
}
