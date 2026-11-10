// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Text;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Samples and laboratory methods. A sample is taken from a patient with a physical tool, put into a machine,
/// and the machine prints a paper report. Each method reveals only what that method can see, and what it
/// learns about a compound is kept as knowledge of the world.
/// </summary>
public sealed partial class NCLabSystem : EntitySystem
{
    private const float InteractionRange = 1.5f;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCLabSamplerComponent, AfterInteractEvent>(OnSamplerInteract);
        SubscribeLocalEvent<NCLabSamplerComponent, NCLabSampleDoAfterEvent>(OnSampleDone);
        SubscribeLocalEvent<NCLabAnalyzerComponent, ActivateInWorldEvent>(OnAnalyzerActivate);
        SubscribeLocalEvent<NCLabAnalyzerComponent, NCLabAnalyzeDoAfterEvent>(OnAnalyzeDone);
    }

    // ---- sampling -------------------------------------------------------------------------------------

    private void OnSamplerInteract(Entity<NCLabSamplerComponent> sampler, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } patient ||
            !HasComp<NCMedicalBodyComponent>(patient))
        {
            return;
        }

        args.Handled = true;
        if (sampler.Comp.Charges <= 0)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-sampler-empty"), sampler, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager, args.User, sampler.Comp.Duration,
            new NCLabSampleDoAfterEvent(), sampler.Owner, target: patient, used: sampler.Owner)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = InteractionRange,
        };
        _doAfter.TryStartDoAfter(doAfter);
    }

    private void OnSampleDone(Entity<NCLabSamplerComponent> sampler, ref NCLabSampleDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } patient)
            return;

        args.Handled = true;
        if (sampler.Comp.Charges <= 0 ||
            !_interaction.InRangeUnobstructed(args.User, patient, InteractionRange))
        {
            return;
        }

        sampler.Comp.Charges--;
        var vial = Spawn(sampler.Comp.Output, Transform(args.User).Coordinates);
        var sample = EnsureComp<NCLabSampleComponent>(vial);
        sample.Kind = sampler.Comp.Kind;
        sample.Source = Name(patient);
        Dirty(vial, sample);
        Capture(patient, EnsureComp<NCLabSampleDataComponent>(vial));
        _hands.TryPickupAnyHand(args.User, vial);
        _popup.PopupEntity(Loc.GetString("nc-lab-sample-taken"), patient, args.User);
    }

    private void Capture(EntityUid patient, NCLabSampleDataComponent data)
    {
        data.Drugs.Clear();
        if (TryComp<NCCirculationComponent>(patient, out var circulation))
            data.BloodGroup = circulation.TransfusionCompatibility;

        if (TryComp<NCPharmacologyComponent>(patient, out var pharmacology))
        {
            foreach (var (drug, exposure) in pharmacology.ActiveDrugs.OrderBy(entry => entry.Key.Id, StringComparer.Ordinal))
            {
                if (exposure.Concentration > 0f)
                    data.Drugs.Add(new NCLabSampleDrug { Drug = drug, Concentration = exposure.Concentration });
            }
        }

        data.WoundInfection = TryComp<NCWoundContainerComponent>(patient, out var wounds) && wounds.Wounds.Count > 0
            ? wounds.Wounds.Values.Max(wound => wound.Infection)
            : 0f;
        data.Strain = _pharmacology.GetStrain(patient);
    }

    // ---- analysis -------------------------------------------------------------------------------------

    private void OnAnalyzerActivate(Entity<NCLabAnalyzerComponent> machine, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        if (!_power.IsPowered(machine.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), machine, args.User);
            return;
        }

        if (GetLoaded(machine) == null)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-empty"), machine, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager, args.User, machine.Comp.Duration,
            new NCLabAnalyzeDoAfterEvent(), machine.Owner, target: machine.Owner)
        {
            BreakOnMove = true,
            DistanceThreshold = InteractionRange,
        };
        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-started"), machine, args.User);
    }

    private EntityUid? GetLoaded(Entity<NCLabAnalyzerComponent> machine)
    {
        return _slots.TryGetSlot(machine.Owner, machine.Comp.Slot, out var slot) ? slot.Item : null;
    }

    private void OnAnalyzeDone(Entity<NCLabAnalyzerComponent> machine, ref NCLabAnalyzeDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(machine.Owner) || GetLoaded(machine) is not { } loaded)
            return;

        var report = new StringBuilder();
        string title;
        if (TryComp<NCLabSampleComponent>(loaded, out var sample) &&
            TryComp<NCLabSampleDataComponent>(loaded, out var data))
        {
            title = Loc.GetString("nc-lab-report-sample", ("source", sample.Source));
            if (machine.Comp.Method == NCLabMethod.Metabolic)
                ReportMetabolic(report, sample, data);
            else
                ReportCulture(report, machine.Comp, sample, data);
        }
        else if (machine.Comp.Method == NCLabMethod.Culture &&
                 TryComp<NCPharmaContainerComponent>(loaded, out var pack) &&
                 _batches.TryGetPackCompound(pack, out var compound))
        {
            title = Loc.GetString("nc-lab-report-compound");
            _batches.RecordAnalysis(compound, NCAnalysisTier.InVitro);
            AppendDossier(report, compound);
            if (_batches.GetPackMasking(pack) > 0.05f)
                report.AppendLine(Loc.GetString("nc-lab-masking-detected"));
            if (_batches.GetPackContaminants(pack) > 0.03f)
                report.AppendLine(Loc.GetString("nc-lab-contaminants-detected"));
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-wrong-sample"), machine, args.User);
            return;
        }

        Print(machine, args.User, title, report.ToString());
    }

    private void ReportCulture(
        StringBuilder report,
        NCLabAnalyzerComponent method,
        NCLabSampleComponent sample,
        NCLabSampleDataComponent data)
    {
        var threshold = sample.Kind == NCLabSampleKind.Blood ? method.BloodCultureThreshold : method.SwabThreshold;
        if (data.WoundInfection < threshold || data.Strain is not { } strainId ||
            !_prototypes.TryIndex(strainId, out var strain))
        {
            report.AppendLine(Loc.GetString("nc-lab-culture-no-growth"));
            return;
        }

        report.AppendLine(Loc.GetString("nc-lab-culture-growth",
            ("level", Loc.GetString(InfectionKey(data.WoundInfection))),
            ("strain", Loc.GetString(strain.Name))));
        report.AppendLine(Loc.GetString("nc-lab-culture-sensitivity"));
        foreach (var drug in _prototypes.EnumeratePrototypes<NCDrugProfilePrototype>()
                     .Where(candidate => candidate.AntibioticPowerAtTherapeutic > 0f && _batches.IsIdentified(candidate.ID))
                     .OrderBy(candidate => candidate.ID, StringComparer.Ordinal))
        {
            var susceptibility = strain.Susceptibility.TryGetValue(drug.ID, out var value) ? value : 1f;
            report.AppendLine(Loc.GetString("nc-lab-culture-row",
                ("drug", Loc.GetString(NCLabText.DrugKey(drug.ID))),
                ("result", Loc.GetString(SusceptibilityKey(susceptibility)))));
        }
    }

    private void ReportMetabolic(StringBuilder report, NCLabSampleComponent sample, NCLabSampleDataComponent data)
    {
        if (sample.Kind != NCLabSampleKind.Blood)
        {
            report.AppendLine(Loc.GetString("nc-lab-metabolic-needs-blood"));
            return;
        }

        report.AppendLine(Loc.GetString("nc-lab-metabolic-group",
            ("group", Loc.GetString("nc-lab-blood-group-" + data.BloodGroup.ToLowerInvariant()))));
        if (data.Drugs.Count == 0)
        {
            report.AppendLine(Loc.GetString("nc-lab-metabolic-clean"));
            return;
        }

        foreach (var entry in data.Drugs)
        {
            var drugId = entry.Drug;
            var concentration = entry.Concentration;
            if (!_prototypes.TryIndex(drugId, out var drug))
                continue;

            // A living organism's response is the deepest method: it confirms everything about the compound.
            _batches.RecordAnalysis(drugId, NCAnalysisTier.InVivo);
            var state = concentration > drug.ToxicConcentration ? "nc-lab-level-toxic"
                : concentration >= drug.MinimumEffectiveConcentration ? "nc-lab-level-therapeutic"
                : "nc-lab-level-trace";
            report.AppendLine(Loc.GetString("nc-lab-metabolic-row",
                ("drug", Loc.GetString(_batches.CompoundNameKey(drugId))),
                ("concentration", concentration.ToString("0.0")),
                ("level", Loc.GetString(state))));
        }

        var combination = _pharmacology.ClassifyCombination(data.Drugs.Select(entry => entry.Drug).ToList());
        report.AppendLine(Loc.GetString("nc-lab-metabolic-interaction",
            ("class", Loc.GetString(NCLabText.InteractionKey(combination)))));
        foreach (var entry in data.Drugs)
        {
            if (_prototypes.TryIndex(entry.Drug, out var drug) && drug.Codename != null)
                AppendDossier(report, entry.Drug);
        }
    }

    /// <summary>Writes the compound card as far as the laboratory knows it.</summary>
    private void AppendDossier(StringBuilder report, ProtoId<NCDrugProfilePrototype> compound)
    {
        report.AppendLine(Loc.GetString("nc-lab-dossier-header", ("name", Loc.GetString(_batches.CompoundNameKey(compound)))));
        foreach (var row in _batches.ReadDossier(compound))
        {
            var label = Loc.GetString(NCLabText.PropertyLabelKey(row.Kind));
            if (row.State == NCCompoundPropertyState.Unknown)
            {
                report.AppendLine(Loc.GetString("nc-lab-dossier-unknown", ("label", label)));
                continue;
            }

            report.AppendLine(Loc.GetString("nc-lab-dossier-row",
                ("label", label),
                ("value", Loc.GetString(NCLabText.PropertyKey(row.Kind, row.Value))),
                ("state", Loc.GetString(NCLabText.StateKey(row.State)))));
        }
    }

    private void Print(EntityUid machine, EntityUid user, string title, string body)
    {
        var paper = Spawn("Paper", Transform(machine).Coordinates);
        _metaData.SetEntityName(paper, Loc.GetString("nc-lab-report-name", ("title", title)));
        if (TryComp<PaperComponent>(paper, out var component))
            _paper.SetContent((paper, component), title + "\n\n" + body);

        _hands.TryPickupAnyHand(user, paper);
        _popup.PopupEntity(Loc.GetString("nc-lab-machine-done"), machine, user);
    }

    private static string InfectionKey(float infection) =>
        infection < 0.3f ? "nc-lab-infection-mild" : infection < 0.6f ? "nc-lab-infection-moderate" : "nc-lab-infection-severe";

    private static string SusceptibilityKey(float susceptibility) =>
        susceptibility >= 0.75f ? "nc-lab-sensitivity-effective"
        : susceptibility >= 0.4f ? "nc-lab-sensitivity-partial"
        : "nc-lab-sensitivity-resistant";
}
