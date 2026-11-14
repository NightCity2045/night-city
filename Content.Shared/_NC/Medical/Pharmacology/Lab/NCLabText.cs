// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>Localization ids of the laboratory, so no report is built from a raw enum or prototype name.</summary>
public static class NCLabText
{
    /// <summary>Key of a compound's name in the shared drug-name table of the diagnostics.</summary>
    public static string DrugKey(string drugId) => "nc-medical-diagnostics-drug-" + Kebab(drugId.StartsWith("NC", StringComparison.Ordinal) ? drugId[2..] : drugId);

    public static string PropertyKey(NCCompoundPropertyKind kind, string value) =>
        $"nc-lab-prop-{KindId(kind)}-{value}";

    public static string PropertyLabelKey(NCCompoundPropertyKind kind) => $"nc-lab-prop-label-{KindId(kind)}";

    public static string StateKey(NCCompoundPropertyState state) => state switch
    {
        NCCompoundPropertyState.Confirmed => "nc-lab-state-confirmed",
        NCCompoundPropertyState.Suspected => "nc-lab-state-suspected",
        _ => "nc-lab-state-unknown",
    };

    public static string InteractionKey(NCDrugInteractionClass value) => value switch
    {
        NCDrugInteractionClass.Compatible => "nc-lab-interaction-compatible",
        NCDrugInteractionClass.Caution => "nc-lab-interaction-caution",
        NCDrugInteractionClass.Dangerous => "nc-lab-interaction-dangerous",
        _ => "nc-lab-interaction-unknown",
    };

    private static string KindId(NCCompoundPropertyKind kind) => kind switch
    {
        NCCompoundPropertyKind.MetabolicRoute => "route",
        NCCompoundPropertyKind.PrimaryAction => "primary",
        NCCompoundPropertyKind.SecondaryAction => "secondary",
        NCCompoundPropertyKind.Duration => "duration",
        NCCompoundPropertyKind.TherapeuticWindow => "window",
        NCCompoundPropertyKind.DependenceRisk => "dependence",
        _ => "interactions",
    };

    private static string Kebab(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(value[i]));
        }

        return builder.ToString();
    }
}
