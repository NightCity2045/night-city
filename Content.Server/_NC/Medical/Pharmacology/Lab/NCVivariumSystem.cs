// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// The vivarium: an observation post for experimental pharmacology and toxicology. The test subjects are real
/// bodies, players and NPCs. A dose given to a patient within sight of a working post is written down as a study:
/// what the patient actually received and how the body took it. It is a way to learn, not a gate: a known
/// formula never has to pass through it.
/// </summary>
[RegisterComponent, Access(typeof(NCVivariumSystem))]
public sealed partial class NCVivariumComponent : Component
{
    /// <summary>How far from the post a patient is observed.</summary>
    [DataField]
    public float Range = 6f;
}

public sealed class NCVivariumSystem : EntitySystem
{
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private NCLabSystem _lab = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaDoseAdministeredEvent>(OnDoseAdministered);
    }

    private void OnDoseAdministered(ref NCPharmaDoseAdministeredEvent args)
    {
        if (!_batches.TryGetTuning(out var tuning) || !_prototypes.TryIndex(args.Compound, out var compound))
            return;

        // Find a working post that can see the patient.
        var patientPosition = _transform.GetMapCoordinates(args.Patient);
        EntityUid? post = null;
        var query = EntityQueryEnumerator<NCVivariumComponent>();
        while (query.MoveNext(out var uid, out var vivarium))
        {
            var position = _transform.GetMapCoordinates(uid);
            if (position.MapId != patientPosition.MapId ||
                (position.Position - patientPosition.Position).Length() > vivarium.Range ||
                !_power.IsPowered(uid))
            {
                continue;
            }

            post = uid;
            break;
        }

        if (post == null)
            return;

        var result = NCPharmaBatchMath.ResolveStudy(tuning, args.Effect, compound.ToxicConcentration);
        var subject = Name(args.Patient);
        if (!_batches.RecordVivariumStudy(args.BatchId, Name(args.Administrator), subject, result))
            return;

        var report = new StringBuilder();
        report.AppendLine(Loc.GetString("nc-lab-vivarium-subject", ("subject", subject)));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-outcome-" + NCPharmaBatchSystem.OutcomeId(result.Outcome)));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-peak", ("peak", result.Peak.ToString("0.##"))));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-ratio", ("ratio", result.ToxicRatio.ToString("P0"))));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-toxin", ("toxin", result.Toxin.ToString("0.##"))));
        report.AppendLine(Loc.GetString("nc-lab-vivarium-sepsis", ("sepsis", result.Sepsis.ToString("0.##"))));
        _lab.AppendDossier(report, args.Compound);
        _lab.Print(post.Value, args.Administrator, Loc.GetString("nc-lab-report-vivarium"), report.ToString(), pickup: false);
    }
}
