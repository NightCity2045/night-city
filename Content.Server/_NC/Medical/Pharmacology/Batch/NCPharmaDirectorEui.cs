// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Text;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Director;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Console;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// The Pharma Director: the whole pharmaceutical truth in one window. Reading is open to administrators;
/// every intervention needs the fun flag and is written to the history of the batch or compound it touches.
/// </summary>
public sealed partial class NCPharmaDirectorEui : BaseEui
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IAdminManager _admin = default!;

    public NCPharmaDirectorEui()
    {
        IoCManager.InjectDependencies(this);
    }

    public override EuiStateBase GetNewState()
    {
        var batches = _entities.System<NCPharmaBatchSystem>();
        var tabs = new NCPharmaDirectorRow[Enum.GetValues<NCPharmaDirectorTab>().Length][];
        tabs[(int) NCPharmaDirectorTab.Overview] = BuildOverview(batches);
        tabs[(int) NCPharmaDirectorTab.Compounds] = BuildCompounds(batches);
        tabs[(int) NCPharmaDirectorTab.Formulas] = BuildFormulas(batches);
        tabs[(int) NCPharmaDirectorTab.Batches] = BuildBatches(batches);
        tabs[(int) NCPharmaDirectorTab.Experiments] = batches.Experiments
            .Select(record => new NCPharmaDirectorRow(record.ExperimentId, batches.DescribeExperiment(record), batches.DescribeExperiment(record)))
            .ToArray();
        tabs[(int) NCPharmaDirectorTab.Cases] = BuildCases(batches);
        tabs[(int) NCPharmaDirectorTab.Journal] = batches.Journal
            .Select((entry, index) => new NCPharmaDirectorRow(
                index.ToString(),
                $"[{Loc.GetString(NCPharmaTextKeys.JournalKind(entry.Kind))}] {entry.FormulaId} {entry.BatchCode} · {entry.Actor}",
                entry.Detail))
            .Reverse()
            .ToArray();
        return new NCPharmaDirectorEuiState(tabs);
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        var batches = _entities.System<NCPharmaBatchSystem>();
        var actor = Player.AttachedEntity is { } attached ? _entities.ToPrettyString(attached).ToString() : Player.Name;
        var mayChange = _admin.HasAdminFlag(Player, AdminFlags.Fun);
        switch (msg)
        {
            case NCPharmaDirectorRefreshMessage:
                StateDirty();
                break;
            case NCPharmaDirectorRecallMessage recall when mayChange:
                batches.TryRecall(recall.BatchId, actor);
                StateDirty();
                break;
            case NCPharmaDirectorAdjustMessage adjust when mayChange:
                batches.TryAdminAdjust(
                    adjust.BatchId,
                    actor,
                    adjust.Purity >= 0f ? adjust.Purity : null,
                    adjust.Sterility >= 0f ? adjust.Sterility : null);
                StateDirty();
                break;
            case NCPharmaDirectorKnowledgeMessage knowledge when mayChange &&
                                                                _prototypes.HasIndex<NCDrugProfilePrototype>(knowledge.Compound):
                var tier = knowledge.Level switch
                {
                    1 => NCAnalysisTier.Chemical,
                    2 => NCAnalysisTier.InVitro,
                    3 => NCAnalysisTier.InVivo,
                    _ => NCAnalysisTier.None,
                };
                if (tier != NCAnalysisTier.None)
                    batches.RecordAnalysis(knowledge.Compound, tier);
                StateDirty();
                break;
        }
    }

    private NCPharmaDirectorRow[] BuildOverview(NCPharmaBatchSystem batches)
    {
        var formulas = batches.AllFormulas.ToList();
        var rows = new List<NCPharmaDirectorRow>
        {
            new("formulas", Loc.GetString("nc-pharma-director-formulas", ("count", formulas.Count),
                ("derived", formulas.Count(f => f.DerivedFrom != null || f.CustomName != null)),
                ("experimental", formulas.Count(f => f.RegulatoryStatus == NCPharmaRegulatoryStatus.Experimental))), string.Empty),
            new("batches", Loc.GetString("nc-pharma-director-batches", ("count", batches.Batches.Count)), string.Empty),
            new("experiments", Loc.GetString("nc-pharma-director-experiments", ("count", batches.Experiments.Count)), string.Empty),
        };
        foreach (var group in batches.Batches.GroupBy(batch => batch.QcState).OrderBy(group => group.Key))
            rows.Add(new NCPharmaDirectorRow(group.Key.ToString(), $"QC {Loc.GetString(NCPharmaTextKeys.QcState(group.Key))}: {group.Count()}", string.Empty));
        return rows.ToArray();
    }

    private NCPharmaDirectorRow[] BuildCompounds(NCPharmaBatchSystem batches)
    {
        var rows = new List<NCPharmaDirectorRow>();
        foreach (var drug in _prototypes.EnumeratePrototypes<NCDrugProfilePrototype>().OrderBy(drug => drug.ID, StringComparer.Ordinal))
        {
            NCDependenceProfilePrototype? dependence = null;
            if (drug.Dependence is { } dependenceId)
                _prototypes.TryIndex(dependenceId, out dependence);

            var detail = new StringBuilder();
            detail.AppendLine(Loc.GetString("nc-pharma-director-compound-known", ("tier", batches.GetKnowledge(drug.ID).ToString())));
            foreach (var property in NCCompoundDossierMath.Build(drug, dependence))
            {
                detail.AppendLine($"{Loc.GetString(NCLabText.PropertyLabelKey(property.Kind))}: " +
                                  $"{Loc.GetString(NCLabText.PropertyKey(property.Kind, property.Value))} [{property.ConfirmedAt}]");
            }

            var name = drug.Codename != null ? batches.CodenameOrId(drug.ID) : batches.CompoundName(drug.ID);
            rows.Add(new NCPharmaDirectorRow(drug.ID, $"{name} · {batches.GetKnowledge(drug.ID)}", detail.ToString()));
        }

        return rows.ToArray();
    }

    private NCPharmaDirectorRow[] BuildFormulas(NCPharmaBatchSystem batches)
    {
        return batches.AllFormulas.OrderBy(formula => formula.ID, StringComparer.Ordinal).Select(formula =>
        {
            var name = formula.CustomName ?? Loc.GetString(formula.Name);
            var detail = $"{formula.Compound} · {formula.DeliveryForm} · {formula.TargetConcentration:0.##} · " +
                         $"{string.Join(", ", formula.Modifiers.Select(modifier => modifier.Id))} · {formula.DerivedFrom} · {formula.Author}";
            return new NCPharmaDirectorRow(formula.ID, $"{name} v{formula.Version} · {formula.RegulatoryStatus}", detail);
        }).ToArray();
    }

    private NCPharmaDirectorRow[] BuildBatches(NCPharmaBatchSystem batches)
    {
        var containers = _entities.System<SharedContainerSystem>();
        return batches.Batches.OrderBy(batch => batch.CreatedAt).Select(batch =>
        {
            var detail = new StringBuilder();
            var label = batch.Label;
            var actual = batch.Actual;
            detail.AppendLine($"label: conc {label.Concentration:0.##} purity {label.Purity:P0} {(label.Sterile ? "sterile" : "not sterile")} {label.Grade}");
            detail.AppendLine($"actual: conc {actual.Concentration:0.##} purity {actual.Purity:P1} sterility {actual.Sterility:P1} " +
                              $"homogeneity {actual.Homogeneity:P1} stability {actual.Stability:P1}");
            detail.AppendLine($"toxicity x{batch.ModifierToxicity:0.##} masking {batch.Masking:P0} contaminants {batch.Contaminants:P0}");
            detail.AppendLine($"made by {batch.Producer} @ {batch.Facility}; doses {batch.DosesProduced}/{batch.DosesPacked}/{batch.DosesAdministered}");
            var query = _entities.EntityQueryEnumerator<NCPharmaContainerComponent>();
            while (query.MoveNext(out var uid, out var pack))
            {
                if (pack.BatchId != batch.BatchId)
                    continue;

                var holder = containers.TryGetContainingContainer(uid, out var container)
                    ? _entities.ToPrettyString(container.Owner).ToString()
                    : "floor";
                detail.AppendLine($"pack {_entities.ToPrettyString(uid)}: {pack.Doses} doses, {holder}");
            }

            foreach (var entry in batch.Audit.TakeLast(20))
                detail.AppendLine($"[{entry.Time.Elapsed:d\\.hh\\:mm\\:ss}] {entry.Kind} {entry.Actor}: {entry.Detail}");

            return new NCPharmaDirectorRow(batch.BatchId.ToString(), $"{batch.BatchCode} · {batch.Formula} v{batch.FormulaVersion} · {batch.QcState}", detail.ToString());
        }).ToArray();
    }

    private NCPharmaDirectorRow[] BuildCases(NCPharmaBatchSystem batches)
    {
        var rows = new List<NCPharmaDirectorRow>();
        var query = _entities.EntityQueryEnumerator<NCPharmacologyComponent>();
        while (query.MoveNext(out var uid, out var state))
        {
            var withdrawing = state.Dependence.Values.Any(record => record.Stage != NCWithdrawalStage.None);
            if (state.ActiveDrugs.Count == 0 && state.OverdoseBurden <= 0f && state.Dependence.Count == 0)
                continue;

            var detail = new StringBuilder();
            foreach (var (id, exposure) in state.ActiveDrugs)
                detail.AppendLine($"{batches.CompoundName(id)}: {exposure.Concentration:0.##} (cumulative {exposure.CumulativeDose:0.##})");
            foreach (var (id, record) in state.Dependence)
            {
                detail.AppendLine($"{batches.CompoundName(id)}: tolerance {record.Tolerance:0.##} dependence {record.Dependence:0.##} " +
                                  $"craving {record.Craving:0.##} stage {record.Stage}");
            }

            rows.Add(new NCPharmaDirectorRow(
                uid.ToString(),
                $"{_entities.GetComponent<MetaDataComponent>(uid).EntityName} · overdose {state.OverdoseBurden:0.##}{(withdrawing ? " · withdrawal" : string.Empty)}",
                detail.ToString()));
        }

        return rows.ToArray();
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCPharmaDirectorWindowCommand : IConsoleCommand
{
    [Dependency] private EuiManager _eui = default!;

    public string Command => "ncpharmadirectorui";
    public string Description => "Open the Pharma Director window.";
    public string Help => "ncpharmadirectorui";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError("This command needs a player.");
            return;
        }

        _eui.OpenEui(new NCPharmaDirectorEui(), player);
    }
}
