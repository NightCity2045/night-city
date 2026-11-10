// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A physical pack of doses from one batch. It stores only the public label: the real composition stays in the
/// server registry and is never replicated, so a modified client cannot read a hidden contaminant.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCPharmaContainerComponent : Component
{
    [DataField, AutoNetworkedField]
    public Guid BatchId;

    [DataField, AutoNetworkedField]
    public int Doses;

    [DataField, AutoNetworkedField]
    public string BatchCode = string.Empty;

    [DataField, AutoNetworkedField]
    public string FormulaId = string.Empty;

    [DataField, AutoNetworkedField]
    public int FormulaVersion;

    [DataField, AutoNetworkedField]
    public float LabelConcentration;

    [DataField, AutoNetworkedField]
    public float LabelPurity;

    [DataField, AutoNetworkedField]
    public bool LabelSterile;

    [DataField, AutoNetworkedField]
    public NCPharmaGrade LabelGrade;
}

/// <summary>Cancellable DoAfter carried while a dose is being administered.</summary>
[Serializable, NetSerializable]
public sealed partial class NCPharmaAdministerDoAfterEvent : SimpleDoAfterEvent;
