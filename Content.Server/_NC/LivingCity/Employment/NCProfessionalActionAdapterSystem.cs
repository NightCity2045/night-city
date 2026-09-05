// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>Routes the universal work action to the existing profession adapter selected by durable employment.</summary>
public sealed partial class NCProfessionalActionAdapterSystem : EntitySystem
{
    private const string WorkAction = "perform-authoritative-work";

    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCProfessionProfileSystem _professions = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (args.Action.ID != WorkAction)
            return;
        args.Handled = true;

        // Abstract work is owned by the schedule simulation and completes only after that activity advances.
        if (args.EntityUid is not { } worker || !Exists(worker))
        {
            if (!_actors.TryGetCached(args.ActorId, out var actor))
                args.Failure = "professional-actor-unavailable";
            else
                args.Completed = !actor.Snapshot.Schedule.CurrentActivity.Equals("work",
                    StringComparison.OrdinalIgnoreCase);
            return;
        }
        if (!_workforce.TryGetAssignment(args.ActorId, out var organization, out var job))
        {
            args.Failure = "professional-employment-missing";
            return;
        }
        if (!_professions.TryGetProfile(args.ActorId, out var profile))
        {
            args.Failure = "professional-profile-missing";
            return;
        }

        var request = new NCProfessionalWorkRequestedEvent(args.ActorId, worker, organization, job, profile);
        RaiseLocalEvent(request);
        if (!request.Handled)
            args.Failure = $"professional-adapter-missing:{profile.ID}";
        else
        {
            args.Completed = request.Completed;
            args.Failure = request.Failure;
        }
    }
}
