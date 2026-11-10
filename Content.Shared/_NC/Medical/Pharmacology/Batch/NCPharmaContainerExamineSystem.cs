// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Examine;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Shows only the public label. What a pack really contains is revealed through laboratory analysis alone.
/// </summary>
public sealed class NCPharmaContainerExamineSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaContainerComponent, ExaminedEvent>(OnExamined);
    }

    private void OnExamined(Entity<NCPharmaContainerComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var comp = ent.Comp;
        var grade = Loc.GetString(GetGradeKey(comp.LabelGrade));
        var sterile = Loc.GetString(comp.LabelSterile ? "nc-pharma-label-sterile" : "nc-pharma-label-not-sterile");
        args.PushMarkup(Loc.GetString(
            "nc-pharma-container-examine",
            ("code", comp.BatchCode),
            ("concentration", comp.LabelConcentration.ToString("0.#")),
            ("purity", comp.LabelPurity.ToString("P0")),
            ("grade", grade),
            ("sterile", sterile),
            ("doses", comp.Doses)));
    }

    /// <summary>Localization key of a market grade, used by every UI that shows batch labels.</summary>
    public static string GetGradeKey(NCPharmaGrade grade) => grade switch
    {
        NCPharmaGrade.Corporate => "nc-pharma-grade-corporate",
        NCPharmaGrade.Medical => "nc-pharma-grade-medical",
        _ => "nc-pharma-grade-street",
    };
}
