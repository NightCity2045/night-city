// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Client.UserInterface.Controls;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Surgery;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Surgery;

/// <summary>
/// Presents the only UI used by direct surgery: a compact radial when the held instrument could begin more
/// than one valid operation. It receives no patient/tool entities and sends back only the offered prototype pair.
/// </summary>
public sealed partial class NCSurgeryDirectClientSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private SimpleRadialMenu? _menu;
    private NCActionResolutionId _offerId;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCSurgeryDirectChoiceOffer>(OnChoiceOffer);
    }

    public override void Shutdown()
    {
        CloseMenu();
        base.Shutdown();
    }

    private void OnChoiceOffer(NCSurgeryDirectChoiceOffer offer)
    {
        CloseMenu();
        if (!offer.OfferId.IsValid || offer.Options.Count is < 2 or > 64)
            return;

        _offerId = offer.OfferId;
        var groups = offer.Options
            .GroupBy(option => (option.TargetCategory, option.TargetName))
            .OrderBy(group => group.Key.TargetName, StringComparer.Ordinal)
            .ToList();

        var root = new List<RadialMenuOptionBase>();
        if (groups.Count == 1)
        {
            root.AddRange(BuildProcedureOptions(groups[0]));
        }
        else
        {
            foreach (var group in groups)
            {
                var procedures = BuildProcedureOptions(group);
                if (procedures.Count == 0)
                    continue;

                root.Add(new RadialMenuNestedLayerOption(procedures, 120)
                {
                    ToolTip = group.Key.TargetName,
                    IconSpecifier = procedures[0].IconSpecifier,
                });
            }
        }

        if (root.Count == 0)
            return;

        _menu = new SimpleRadialMenu();
        _menu.SetButtons(root, new SimpleRadialMenuSettings
        {
            DefaultContainerRadius = 115,
            UseSectors = true,
            DisplayBorders = true,
        });
        _menu.OnClose += OnMenuClosed;
        _menu.OpenOverMouseScreenPosition();
    }

    private List<RadialMenuOptionBase> BuildProcedureOptions(
        IEnumerable<NCSurgeryDirectProcedureOption> options)
    {
        var result = new List<RadialMenuOptionBase>();
        foreach (var option in options.OrderBy(value => ProcedureName(value.Procedure), StringComparer.Ordinal))
        {
            var button = new RadialMenuActionOption<NCSurgeryDirectProcedureOption>(SelectProcedure, option)
            {
                ToolTip = $"{ProcedureName(option.Procedure)} — {option.TargetName}",
                IconSpecifier = ResolveToolIcon(option.Procedure),
            };
            result.Add(button);
        }

        return result;
    }

    private void SelectProcedure(NCSurgeryDirectProcedureOption option)
    {
        var offerId = _offerId;
        CloseMenu();
        if (!offerId.IsValid)
            return;

        RaiseNetworkEvent(new NCSurgeryDirectChoiceRequest(
            offerId,
            option.Procedure,
            option.TargetCategory));
    }

    private RadialMenuIconSpecifier? ResolveToolIcon(ProtoId<NCSurgeryProcedurePrototype> procedureId)
    {
        if (!_prototypes.TryIndex(procedureId, out NCSurgeryProcedurePrototype? procedure))
            return null;

        foreach (var stepId in procedure.Steps)
        {
            if (!_prototypes.TryIndex(stepId, out NCSurgeryStepPrototype? step))
                return null;
            if (step.OptionalAtDirectStart)
                continue;
            if (step.Tools.Count == 0 ||
                !_prototypes.TryIndex(step.Tools[0].Capability, out NCSurgeryToolCapabilityPrototype? capability))
            {
                return null;
            }

            return RadialMenuIconSpecifier.With(capability.RepresentativeTool);
        }

        return null;
    }

    private string ProcedureName(ProtoId<NCSurgeryProcedurePrototype> id)
    {
        var key = $"nc-medical-surgery-procedure-{PrototypeKey(id.Id, "NCSurgery")}";
        if (Loc.TryGetString(key, out var localized))
            return localized;

        return _prototypes.TryIndex(id, out NCSurgeryProcedurePrototype? procedure)
            ? procedure.Name
            : id.Id;
    }

    private static string PrototypeKey(string id, string prefix)
    {
        var value = id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;
        if (value.Length == 0)
            return string.Empty;

        var chars = new List<char>(value.Length * 2);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) &&
                (char.IsLower(value[index - 1]) || index + 1 < value.Length && char.IsLower(value[index + 1])))
            {
                chars.Add('-');
            }

            chars.Add(char.ToLowerInvariant(character));
        }

        return new string(chars.ToArray());
    }

    private void CloseMenu()
    {
        if (_menu == null)
            return;

        _menu.OnClose -= OnMenuClosed;
        _menu.Close();
        _menu = null;
        _offerId = default;
    }

    private void OnMenuClosed()
    {
        if (_menu != null)
            _menu.OnClose -= OnMenuClosed;
        _menu = null;
        _offerId = default;
    }
}
