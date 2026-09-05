// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Validates and indexes professional cognition overlays. Lookups are keyed by ActorId/job and never scan ECS
/// entities, allowing every organization to extend the same human runtime without spawning a bespoke brain.
/// </summary>
public sealed partial class NCProfessionProfileSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;

    private readonly Dictionary<ProtoId<JobPrototype>, NCProfessionProfilePrototype> _byJob = [];

    public int ProfileCount => _byJob.Values.Distinct().Count();
    public int BranchCount => _byJob.Values.Select(static value => (value.Organization, value.BranchKey))
        .Distinct().Count();
    public int JobCount => _byJob.Count;
    public string? ConfigurationError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        Rebuild();
    }

    public void AddCapabilityOverlays(NCActorId actorId, List<ProtoId<NCCognitionCapabilityPrototype>> sink)
    {
        if (!_workforce.TryGetAssignment(actorId, out _, out var job) || !_byJob.TryGetValue(job, out var profile))
            return;
        foreach (var capability in profile.CapabilityOverlays)
        {
            if (!sink.Contains(capability))
                sink.Add(capability);
        }
    }

    public bool TryGetProfile(NCActorId actorId, out NCProfessionProfilePrototype profile)
    {
        if (_workforce.TryGetAssignment(actorId, out _, out var job) && _byJob.TryGetValue(job, out profile!))
            return true;
        profile = default!;
        return false;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCProfessionProfilePrototype>() || args.WasModified<DepartmentPrototype>() ||
            args.WasModified<JobPrototype>() || args.WasModified<NCCognitionCapabilityPrototype>())
            Rebuild();
    }

    private void Rebuild()
    {
        _byJob.Clear();
        ConfigurationError = null;
        foreach (var profile in _prototypes.EnumeratePrototypes<NCProfessionProfilePrototype>()
                     .OrderBy(value => value.ID, StringComparer.Ordinal))
        {
            if (!_prototypes.TryIndex(profile.Organization, out DepartmentPrototype? department) ||
                department == null || !department.NCSelectable || profile.Jobs.Count == 0 ||
                !IsStableKey(profile.BranchKey))
            {
                Reject($"Profession profile {profile.ID} has no valid Night City organization, branch, or jobs.");
                return;
            }

            foreach (var capability in profile.CapabilityOverlays)
            {
                if (!_prototypes.HasIndex(capability))
                {
                    Reject($"Profession profile {profile.ID} references missing capability {capability}.");
                    return;
                }
            }

            foreach (var jobId in profile.Jobs)
            {
                if (!department.Roles.Contains(jobId) || !_prototypes.TryIndex(jobId, out JobPrototype? job) ||
                    job == null || !job.NCNpcAllowed ||
                    !department.NCPositionRanks.ContainsKey(jobId) ||
                    profile.RequiresStartingGear && job.StartingGear == null ||
                    !_byJob.TryAdd(jobId, profile))
                {
                    Reject($"Profession profile {profile.ID} references a foreign, unranked, unequipped, player-only, or duplicate job {jobId}.");
                    return;
                }
            }
        }
    }

    private static bool IsStableKey(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(static character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private void Reject(string error)
    {
        ConfigurationError = error;
        _byJob.Clear();
        Log.Error(error);
    }
}
