// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Anatomy;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Medical.Diagnostics;

public sealed partial class NCDiagnosticWindow
{
    private int _selectedCyberwareIndex;
    private NCMedicalCyberwareSummary? _selectedCyberware;

    private void PopulateCyberware(NCMedicalSnapshot snapshot)
    {
        if (!Has(snapshot.Disclosures, NCDiagnosticDisclosure.Cyberware))
        {
            AddUnavailable(CyberwareContainer, NCDiagnosticTier.ScannerInternal);
            return;
        }

        var modules = snapshot.Cyberware.Take(MaximumRowsPerSection).ToArray();
        CyberwareCount.Text = Loc.GetString("nc-cyberware-map-count", ("count", modules.Length));
        CyberwareMap.SetModules(modules,
            _prototypes.EnumeratePrototypes<NCSkeletalSlotPrototype>().OrderBy(slot => slot.Order).ToArray(),
            CyberwareIcon, CyberwareTooltip,
            module => BandColor(CyberwareSeverity(module)),
            module => _prototypes.TryIndex(module.Profile, out NCCyberwarePrototype? profile) && profile.SkeletalEnhancement,
            module => _prototypes.TryIndex(module.Profile, out NCCyberwarePrototype? profile) && profile.DermalArmor);
        if (modules.Length == 0)
        {
            AddEmpty(CyberwareContainer);
            AddOmitted(CyberwareContainer, snapshot.CyberwareOmitted);
            return;
        }

        if (_selectedCyberware is { } selected)
        {
            // Condition changes must not move selection to another row when a refreshed scan is reordered.
            var match = Array.FindIndex(modules, module => module.Profile == selected.Profile &&
                module.HostCategory == selected.HostCategory && module.Slot == selected.Slot);
            _selectedCyberwareIndex = Math.Max(0, match);
        }
        _selectedCyberwareIndex = Math.Clamp(_selectedCyberwareIndex, 0, modules.Length - 1);
        _selectedCyberware = modules[_selectedCyberwareIndex];
        CyberwareMap.Select(_selectedCyberwareIndex);
        AddCyberwareDetails(modules[_selectedCyberwareIndex]);
        AddOmitted(CyberwareContainer, snapshot.CyberwareOmitted);
    }

    private void OnCyberwareSelected(int index)
    {
        if (_snapshot is not { } snapshot || index < 0 || index >= Math.Min(snapshot.Cyberware.Count, MaximumRowsPerSection) ||
            !Has(snapshot.Disclosures, NCDiagnosticDisclosure.Cyberware))
            return;

        _selectedCyberwareIndex = index;
        _selectedCyberware = snapshot.Cyberware[index];
        CyberwareMap.Select(index);
        CyberwareContainer.RemoveAllChildren();
        AddCyberwareDetails(snapshot.Cyberware[index]);
        AddOmitted(CyberwareContainer, snapshot.CyberwareOmitted);
    }

    private string CyberwarePlacement(NCMedicalCyberwareSummary module) => module.Slot is { } slot
        ? (module.HostCategory is { } category ? OrganName(category.Id) + " · " : string.Empty) + CyberwareSlotName(slot)
        : module.HostCategory is { } host ? OrganName(host.Id) : Value(module.Form);

    private string CyberwareTooltip(NCMedicalCyberwareSummary module) =>
        $"{CyberwareName(module.Profile)}\n{CyberwarePlacement(module)}\n" +
        Loc.GetString(module.Operational
            ? "nc-medical-diagnostics-cyberware-online"
            : "nc-medical-diagnostics-cyberware-offline") +
        (module.EmpDisabled ? "\n" + Loc.GetString("nc-cyberware-map-emp") : string.Empty) + NeuralDependencyDetails(module);

    private string NeuralDependencyDetails(NCMedicalCyberwareSummary module)
    {
        var message = module.NeuralDependency switch
        {
            NCCyberwareNeuralDependencyState.Missing => "nc-cyberware-neural-missing",
            NCCyberwareNeuralDependencyState.Unavailable => "nc-cyberware-neural-unavailable",
            _ => null,
        };
        var details = message == null ? string.Empty : "\n" + Loc.GetString(message);
        if (module.NeuralFoundationIndex is { } index && _snapshot is { } snapshot &&
            index >= 0 && index < snapshot.Cyberware.Count)
            details += "\n" + Loc.GetString("nc-cyberware-neural-linked",
                ("host", CyberwareName(snapshot.Cyberware[index].Profile)));
        return details;
    }

    private static NCDiagnosticBand CyberwareSeverity(NCMedicalCyberwareSummary module) =>
        !module.Operational || module.EmpDisabled
            ? NCDiagnosticBand.Critical
            : MaxBand(MaxBand(module.Integrity, module.Function), module.Heat);

    private void AddCyberwareDetails(NCMedicalCyberwareSummary module)
    {
        var state = Loc.GetString(module.Operational
            ? "nc-medical-diagnostics-cyberware-online"
            : "nc-medical-diagnostics-cyberware-offline");
        var details = Loc.GetString("nc-medical-diagnostics-cyberware-summary",
            ("placement", CyberwarePlacement(module)), ("neural", Value(module.NeuralState)), ("state", state));
        if (module.EmpDisabled)
            details += " · " + Loc.GetString("nc-cyberware-map-emp");
        if (module.HostIndex is { } host && _snapshot is { } snapshot && host >= 0 && host < snapshot.Cyberware.Count)
            details += "\n" + Loc.GetString("nc-cyberware-map-installed-in", ("host", CyberwareName(snapshot.Cyberware[host].Profile)));
        details += NeuralDependencyDetails(module);
        if (module.Quality is { } qualityId && _prototypes.TryIndex(qualityId, out var quality))
            details += "\n" + Loc.GetString("nc-medical-part-quality", ("quality", Loc.GetString(quality.Name)));
        if (module.Medical is { } medical)
        {
            details += "\n" + Loc.GetString(medical.ControllerReady ? "nc-medical-controller-ready" : "nc-medical-controller-unready");
            if (medical.Injector)
                details += "\n" + Loc.GetString("nc-medical-scanner-injector", ("remaining", medical.Remaining),
                    ("dose", medical.Dose), ("interval", medical.Interval), ("delivered", medical.Delivered),
                    ("limit", medical.Limit), ("cooldown", MathF.Ceiling(medical.Cooldown)),
                    ("automatic", Loc.GetString(medical.Automatic ? "nc-medical-implant-on" : "nc-medical-implant-off")));
        }
        if (_prototypes.TryIndex(module.Profile, out NCCyberwarePrototype? definition) && (definition.SkeletalEnhancement || definition.DermalArmor || definition.SkeletalSurgery.Count > 0))
        {
            if (definition.DermalArmor)
                details += "\n" + Loc.GetString("nc-dermal-diagnostics");
            if (definition.SkeletalEnhancement)
                details += "\n" + Loc.GetString("nc-skeletal-diagnostics-allowance",
                ("current", module.HardsuitAllowancePercent ?? 0),
                ("maximum", (int) MathF.Round(definition.HardsuitPenaltyAllowance * 100f)));
            if (definition.SkeletalSurgery.Count > 0)
            {
                foreach (var step in definition.SkeletalSurgery)
                    details += "\n" + Loc.GetString("nc-skeletal-diagnostics-step", ("step", Loc.GetString(step.Name)),
                        ("state", Loc.GetString((module.SkeletalSurgeryCompleted & step.Stage) != 0
                            ? "nc-skeletal-surgery-done" : "nc-skeletal-surgery-pending")));
                if (definition.RequiresSkeletalLoadTest)
                    details += "\n" + Loc.GetString("nc-skeletal-diagnostics-step",
                        ("step", Loc.GetString("nc-skeletal-surgery-test")),
                        ("state", Loc.GetString(module.SkeletalLoadTestPassed
                            ? "nc-skeletal-surgery-done" : "nc-skeletal-surgery-pending")));
            }
            if (definition.RequiredSupportProfile != null)
                details += "\n" + Loc.GetString("nc-skeletal-diagnostics-grafts",
                    ("current", module.AvailableSupports ?? 0), ("required", definition.RequiredSupportCount));
        }
        AddCard(CyberwareContainer, CyberwareName(module.Profile), details, BandColor(CyberwareSeverity(module)),
            icon: CyberwareIcon(module), metrics:
            [
                new DiagnosticMetric(Loc.GetString("nc-medical-diagnostics-metric-integrity"),
                    FunctionBandName(module.Integrity), RemainingProgress(module.Integrity), BandColor(module.Integrity)),
                new DiagnosticMetric(Loc.GetString("nc-medical-diagnostics-metric-function"),
                    FunctionBandName(module.Function), RemainingProgress(module.Function), BandColor(module.Function)),
                new DiagnosticMetric(Loc.GetString("nc-medical-diagnostics-metric-power"),
                    FunctionBandName(module.Power), RemainingProgress(module.Power), BandColor(module.Power)),
                new DiagnosticMetric(Loc.GetString("nc-medical-diagnostics-metric-heat"),
                    BandName(module.Heat), BandProgress(module.Heat), BandColor(module.Heat)),
            ], metricColumns: 4);

        if (module.Parts is not { Count: > 0 } parts)
            return;

        CyberwareContainer.AddChild(new Label
        {
            Text = Loc.GetString("nc-cyberware-map-assembly-title"), FontColorOverride = MutedColor,
        });
        var row = new GridContainer { Columns = 4, HSeparationOverride = 8, VSeparationOverride = 8 };
        foreach (var part in parts.Take(8))
        {
            var name = _prototypes.TryIndex(part.Slot, out NCCyberwarePartSlotPrototype? slot)
                ? Loc.GetString(slot.Name) : Loc.GetString("nc-cyberware-map-part-unknown");
            var present = part.EntityPrototype is { } entity && _prototypes.TryIndex(entity, out _);
            var tile = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                SeparationOverride = 3,
            };
            var frame = new PanelContainer
            {
                MinSize = new System.Numerics.Vector2(52, 52),
                MouseFilter = Control.MouseFilterMode.Pass,
                ToolTip = name + "\n" + (present ? FunctionBandName(part.Integrity) : Loc.GetString(part.Optional ? "nc-optics-option-empty" : "nc-cyberware-map-part-missing")),
                PanelOverride = new StyleBoxFlat
                {
                    BackgroundColor = Color.FromHex("#07161D"), BorderColor = !present && part.Optional ? MutedColor : BandColor(part.Integrity),
                    BorderThickness = new Thickness(1),
                },
            };
            if (part.EntityPrototype is { } partPrototype && _prototypes.TryIndex(partPrototype, out var partEntity))
                frame.AddChild(new TextureRect
                {
                    Texture = _sprites.Frame0(partEntity), MinSize = new System.Numerics.Vector2(42, 42),
                    Margin = new Thickness(4), Stretch = TextureRect.StretchMode.KeepAspectCentered,
                });
            else
                frame.AddChild(new Label { Text = "—", FontColorOverride = part.Optional ? MutedColor : CriticalColor });
            tile.AddChild(frame);
            if (part.Quality is { } partQuality && _prototypes.TryIndex(partQuality, out var grade))
                tile.AddChild(new Label { Text = Loc.GetString(grade.Name), FontColorOverride = MutedColor });
            if (part.Firmware is { } firmware && _prototypes.TryIndex(firmware, out var program))
                tile.AddChild(new Label { Text = Loc.GetString(program.Name), FontColorOverride = MutedColor });
            tile.AddChild(new Label { Text = name, FontColorOverride = MutedColor });
            row.AddChild(tile);
        }
        CyberwareContainer.AddChild(row);
    }
}
