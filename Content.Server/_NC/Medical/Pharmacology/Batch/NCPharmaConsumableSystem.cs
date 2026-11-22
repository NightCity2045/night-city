// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Examine;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A vial of catalyst. The reactor takes it when a run starts and the run can add the catalyst that many times;
/// without a vial the operator has none to add.
/// </summary>
[RegisterComponent]
public sealed partial class NCPharmaCatalystComponent : Component
{
    [DataField]
    public int Charges = 2;
}

/// <summary>A process additive that a modifier asks for, such as the stabilizer. The synthesizer uses one up per run.</summary>
[RegisterComponent]
public sealed partial class NCPharmaAdditiveComponent : Component;

public sealed class NCPharmaConsumableSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaCatalystComponent, ExaminedEvent>(OnCatalystExamined);
    }

    private void OnCatalystExamined(Entity<NCPharmaCatalystComponent> vial, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("nc-pharma-catalyst-examine", ("charges", vial.Comp.Charges)));
    }
}
