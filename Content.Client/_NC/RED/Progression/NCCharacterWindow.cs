// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.RED.Progression;
using Content.Shared._NC.Legal;
using Content.Shared._NC.Organizations;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Numerics;

namespace Content.Client._NC.RED.Progression;

/// <summary>
/// Readable client view of server-authoritative persistent state.
/// Skill buttons submit requests; they never mutate the displayed rank optimistically.
/// </summary>
public sealed class NCCharacterWindow : DefaultWindow
{
    private readonly IPrototypeManager _prototypes;
    private readonly Action<string, int> _allocate;
    private readonly BoxContainer _content;
    private NCSkillConfirmationWindow? _confirmation;

    public NCCharacterWindow(IPrototypeManager prototypes, Action<string, int> allocate)
    {
        _prototypes = prototypes;
        _allocate = allocate;
        Title = Loc.GetString("nc-character-window-title");
        MinSize = SetSize = new Vector2(460, 520);

        var scroll = new ScrollContainer();
        _content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 6,
            Margin = new Thickness(10),
        };
        scroll.AddChild(_content);
        ContentsContainer.AddChild(scroll);
    }

    public void UpdateState(NCProgressionStateEvent state)
    {
        _content.RemoveAllChildren();
        _content.AddChild(new Label
        {
            Text = Loc.GetString("nc-character-window-name", ("name", state.CharacterName)),
        });
        _content.AddChild(new Label
        {
            Text = Loc.GetString("nc-character-window-summary",
                ("level", state.Level),
                ("rounds", state.CompletedRounds),
                ("available", state.TotalSkillPoints - state.SpentSkillPoints),
                ("balance", state.BankBalance)),
        });
        _content.AddChild(new Label
        {
            Text = GetLevelProgress(state),
        });
        _content.AddChild(new Label
        {
            Text = Loc.GetString("nc-character-window-assets",
                ("properties", state.PropertyCount),
                ("businesses", state.BusinessCount)),
        });
        _content.AddChild(new Label
        {
            Text = Loc.GetString(
                "nc-character-window-employment",
                ("organization", GetOrganizationName(state.OrganizationPrototypeId)),
                ("department", GetDepartmentName(state.DepartmentPrototypeId)),
                ("position", GetPositionName(state.PositionPrototypeId))),
        });

        if (state.Error != null)
            _content.AddChild(new Label { Text = Loc.GetString(state.Error) });

        AddSection("nc-character-window-properties");
        foreach (var property in state.Properties)
        {
            _content.AddChild(new Label
            {
                Text = Loc.GetString(
                    "nc-character-window-property",
                    ("type", property.PropertyType),
                    ("share", property.ShareBasisPoints / 100f),
                    ("status", GetPropertyStatus(property.Status))),
            });
        }
        if (state.Properties.Length == 0)
            AddEmpty();

        AddSection("nc-character-window-businesses");
        foreach (var business in state.Businesses)
        {
            _content.AddChild(new Label
            {
                Text = Loc.GetString(
                    "nc-character-window-business",
                    ("name", business.Name),
                    ("type", business.BusinessType),
                    ("share", business.ShareBasisPoints / 100f),
                    ("coowners", business.CoownerCount),
                    ("status", GetBusinessStatus(business.Status))),
            });
        }
        if (state.Businesses.Length == 0)
            AddEmpty();

        AddSection("nc-character-window-licenses");
        foreach (var license in state.Licenses)
        {
            _content.AddChild(new Label
            {
                Text = Loc.GetString(
                    "nc-character-window-legal-record",
                    ("name", GetLicenseName(license.PrototypeId)),
                    ("status", GetLegalStatus(license.Status)),
                    ("expiry", FormatExpiry(license.ExpiresAt))),
            });
        }
        if (state.Licenses.Length == 0)
            AddEmpty();

        AddSection("nc-character-window-documents");
        foreach (var document in state.Documents)
        {
            _content.AddChild(new Label
            {
                Text = Loc.GetString(
                    "nc-character-window-document",
                    ("name", GetDocumentName(document.PrototypeId)),
                    ("serial", document.SerialNumber ?? "—"),
                    ("status", GetLegalStatus(document.Status))),
            });
        }
        if (state.Documents.Length == 0)
            AddEmpty();

        AddSection("nc-character-window-skills");
        foreach (var skill in _prototypes.EnumeratePrototypes<NCRedSkillPrototype>()
                     .OrderBy(skill => Loc.GetString(skill.Name)))
        {
            state.Skills.TryGetValue(skill.ID, out var rank);
            var button = new Button
            {
                Text = Loc.GetString("nc-character-window-skill",
                    ("skill", Loc.GetString(skill.Name)),
                    ("rank", rank),
                    ("max", skill.MaxRank),
                    ("cost", skill.CostPerRank)),
                Disabled = rank >= skill.MaxRank ||
                           state.TotalSkillPoints - state.SpentSkillPoints < skill.CostPerRank,
                ToolTip = Loc.GetString(skill.Description),
            };
            var requestedRank = rank + 1;
            button.OnPressed += _ =>
            {
                _confirmation?.Close();
                _confirmation = new NCSkillConfirmationWindow(
                    Loc.GetString(skill.Name),
                    rank,
                    requestedRank,
                    skill.CostPerRank,
                    state.TotalSkillPoints - state.SpentSkillPoints - skill.CostPerRank,
                    () => _allocate(skill.ID, requestedRank));
                _confirmation.OpenCentered();
            };
            _content.AddChild(button);
        }
    }

    private void AddSection(string locId)
    {
        _content.AddChild(new Label { Text = Loc.GetString(locId) });
    }

    private void AddEmpty()
    {
        _content.AddChild(new Label { Text = Loc.GetString("nc-character-window-empty") });
    }

    private string GetLevelProgress(NCProgressionStateEvent state)
    {
        if (!_prototypes.TryIndex<NCRedProgressionPrototype>("NCDefaultProgression", out var progression) ||
            state.Level >= progression.CompletedRoundThresholds.Count)
            return Loc.GetString("nc-character-window-level-max");
        return Loc.GetString(
            "nc-character-window-level-progress",
            ("current", state.CompletedRounds),
            ("required", progression.CompletedRoundThresholds[state.Level]));
    }

    private string GetPositionName(string? prototypeId)
    {
        if (prototypeId == null ||
            !_prototypes.TryIndex<Content.Shared._NC.Organizations.NCPositionPrototype>(
                prototypeId,
                out var position))
            return Loc.GetString("nc-character-window-unemployed");
        return Loc.GetString(position.Name);
    }

    private string GetOrganizationName(string? prototypeId)
    {
        return prototypeId != null &&
               _prototypes.TryIndex<NCOrganizationPrototype>(prototypeId, out var organization)
            ? Loc.GetString(organization.Name)
            : Loc.GetString("nc-character-window-unemployed");
    }

    private string GetDepartmentName(string? prototypeId)
    {
        return prototypeId != null &&
               _prototypes.TryIndex<NCDepartmentPrototype>(prototypeId, out var department)
            ? Loc.GetString(department.Name)
            : "—";
    }

    private string GetLicenseName(string prototypeId)
    {
        return _prototypes.TryIndex<NCLicensePrototype>(prototypeId, out var license)
            ? Loc.GetString(license.Name)
            : prototypeId;
    }

    private string GetDocumentName(string prototypeId)
    {
        return _prototypes.TryIndex<NCDocumentPrototype>(prototypeId, out var document)
            ? Loc.GetString(document.Name)
            : prototypeId;
    }

    private static string GetPropertyStatus(byte status)
    {
        return Loc.GetString(status switch
        {
            0 => "nc-character-status-active",
            1 => "nc-character-status-inheritance",
            2 => "nc-character-status-archived",
            _ => "nc-character-status-invalid",
        });
    }

    private static string GetBusinessStatus(byte status)
    {
        return Loc.GetString(status switch
        {
            0 => "nc-character-status-active",
            1 => "nc-character-status-suspended",
            2 => "nc-character-status-inheritance",
            3 => "nc-character-status-closed",
            _ => "nc-character-status-invalid",
        });
    }

    private static string GetLegalStatus(byte status)
    {
        return Loc.GetString(status switch
        {
            0 => "nc-character-status-active",
            1 => "nc-character-status-expired",
            2 => "nc-character-status-revoked",
            _ => "nc-character-status-invalid",
        });
    }

    private static string FormatExpiry(DateTime? expiry)
    {
        return expiry?.ToString("yyyy-MM-dd") ?? Loc.GetString("nc-character-window-no-expiry");
    }
}
