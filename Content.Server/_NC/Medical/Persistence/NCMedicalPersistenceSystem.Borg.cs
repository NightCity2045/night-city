// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware.Borg;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Server.Database;
using Content.Shared._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Cyberware.Borg;
using Content.Shared.Body;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Mind;
using Content.Shared._NC.Identity;
using Content.Shared.GameTicking;
using Robust.Shared.Player;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    [Dependency] private SharedSolutionContainerSystem _borgSolutions = default!;
    [Dependency] private NCBorgSystem _borg = default!;
    [Dependency] private SharedMindSystem _borgMind = default!;
    [Dependency] private MetaDataSystem _borgMeta = default!;
    [Dependency] private ISharedPlayerManager _borgPlayers = default!;
    private readonly Dictionary<NCCharacterId, EntityUid> _borgCharacters = [];

    /// <summary>Living City actors require their own ownership coordinator; never silently detach their identity.</summary>
    public bool CanTransferBorgContinuity(EntityUid source, EntityUid destination)
    {
        return !TerminatingOrDeleted(source) && !TerminatingOrDeleted(destination) &&
               !HasComp<NCMedicalPersistenceOwnerComponent>(destination) &&
               !_loadingBodies.Contains(source) && !_loadingBodies.Contains(destination) &&
               PendingChemistryFits(source, destination) &&
               (!TryComp<NCMedicalPersistenceOwnerComponent>(source, out var owner) ||
                !owner.SaveBlocked && owner.Kind != NCMedicalStableOwnerKind.PersistentActor);
    }

    /// <summary>
    /// Hands the existing character's binding to its real brain's new medical carrier before BrainSystem moves
    /// the mind. No copy of the mind, ID or brain is created. Technical rollback restores the original graph.
    /// </summary>
    public bool TryTransferBrainToBorg(EntityUid source, EntityUid destination)
    {
        if (!CanTransferBorgContinuity(source, destination) ||
            !HasComp<NCBorgCoreComponent>(destination) ||
            SerializeMedicalState(source) == null ||
            !_pharmacology.TryCapturePersistentState(source, _timing.CurTime, out var drugs))
            return false;
        var previousPhysiology = CapturePhysiology(destination);
        var previousSystemic = CaptureSystemic(destination);
        var physiology = CapturePhysiology(source);
        if (physiology.Irreversible)
            return false;
        var systemic = CaptureSystemic(source);
        // A fresh pod has no pharmacology component: successful capture returns a valid null snapshot.
        if (!_pharmacology.TryCapturePersistentState(destination, _timing.CurTime, out var previousDrugs) ||
            !TryMovePlayerBinding(source, destination))
            return false;
        ApplyPhysiologyState(destination, physiology);
        ApplySystemicState(destination, systemic);
        _pharmacology.TryRestorePersistentState(destination, drugs, _timing.CurTime);
        if (!_detachment.TryTransferOrgan(source, destination, "Brain", "Head"))
        {
            ApplyPhysiologyState(destination, previousPhysiology);
            ApplySystemicState(destination, previousSystemic);
            _pharmacology.TryRestorePersistentState(destination, previousDrugs, _timing.CurTime);
            TryMovePlayerBinding(destination, source);
            return false;
        }
        _pharmacology.ResetBody(source);
        // Pending, unassimilated injected chemicals also follow the physical support circuit. Transfer only
        // non-blood reagents; the donor's actual blood and wounds stay on its abandoned organic body.
        MovePendingBorgChemistry(source, destination);
        _borgMeta.SetEntityName(destination, Name(source));
        return true;
    }

    private bool TryMovePlayerBinding(EntityUid source, EntityUid destination)
    {
        if (_playerBindings.ContainsKey(destination))
            return false;
        if (_playerBindings.Remove(source, out var binding))
        {
            _playerBindings[destination] = binding;
            _boundCharacters[binding.CharacterId] = destination;
        }
        if (TryComp<NCMedicalPersistenceOwnerComponent>(source, out var owner))
        {
            BindOwner(destination, owner.Kind, owner.Key, owner.Revision);
            if (owner.Kind == NCMedicalStableOwnerKind.PlayerCharacter && int.TryParse(owner.Key, out var number))
            {
                var character = new NCCharacterId(number);
                if (HasComp<NCBorgCoreComponent>(destination))
                {
                    _borgCharacters[character] = destination;
                    // An offline patient's physical brain can be converted too. Its owner remains indexed
                    // and checkpointed even while there is no attached session.
                    if (!_playerBindings.ContainsKey(destination))
                    {
                        var state = _saveStates.GetValueOrDefault(character) ?? new OwnerSaveState
                        {
                            ExpectedRevision = owner.Revision,
                        };
                        _saveStates[character] = state;
                        _playerBindings[destination] = new PlayerBinding(character, state);
                        _boundCharacters[character] = destination;
                    }
                }
                else if (_borgCharacters.GetValueOrDefault(character) == source)
                    _borgCharacters.Remove(character);
            }
            RemComp<NCMedicalPersistenceOwnerComponent>(source);
        }
        return true;
    }

    private void MovePendingBorgChemistry(EntityUid source, EntityUid destination)
    {
        foreach (var name in new[] { "bloodstream", "metabolites" })
        {
            if (!_borgSolutions.TryGetSolution(source, name, out var original, out var blood) ||
                !_borgSolutions.TryGetSolution(destination, name, out var target, out _))
                continue;
            foreach (var reagent in blood.Contents.ToArray())
            {
                if (reagent.Reagent.Prototype == "Blood")
                    continue;
                var removed = _borgSolutions.RemoveReagent(original.Value, reagent);
                _borgSolutions.TryAddReagent(target.Value, reagent.Reagent, removed, out _);
            }
        }
    }

    private bool PendingChemistryFits(EntityUid source, EntityUid destination)
    {
        foreach (var name in new[] { "bloodstream", "metabolites" })
        {
            if (!_borgSolutions.TryGetSolution(source, name, out _, out var original) ||
                !_borgSolutions.TryGetSolution(destination, name, out _, out var target))
                return false;
            var amount = original.Contents.Where(entry => entry.Reagent.Prototype != "Blood")
                .Aggregate(FixedPoint2.Zero, (total, entry) => total + entry.Quantity);
            if (amount > target.MaxVolume - target.Volume)
                return false;
        }
        return true;
    }

    private NCBorgCoreSnapshot? CaptureBorgCore(EntityUid body)
    {
        if (!TryComp<NCBorgCoreComponent>(body, out var core))
            return null;
        return new NCBorgCoreSnapshot
        {
            CoreId = core.CoreId,
            Occupied = core.Occupied,
            Temperature = core.Temperature,
            EmpRemaining = TimeSpan.FromTicks(Math.Max(0, (core.EmpDisabledUntil - _timing.CurTime).Ticks)),
            PowerBus = _cyberware.CapturePowerBus(body)!,
            Support = CaptureBorgSolution(body, NCBorgSystem.SupportSolution),
            Bloodstream = CaptureBorgSolution(body, "bloodstream"),
            Metabolites = CaptureBorgSolution(body, "metabolites"),
            Stomach = TryGetBorgOrgan(body, "Stomach", out var stomach)
                ? CaptureBorgSolution(stomach, "stomach") : new Solution(),
            ConsumptionRemainders = new(core.ConsumptionRemainders),
        };
    }

    private bool TryGetBorgOrgan(EntityUid body, ProtoId<OrganCategoryPrototype> category, out EntityUid organ)
    {
        organ = default;
        if (!TryComp<BodyComponent>(body, out var graph) || graph.Organs == null)
            return false;
        foreach (var candidate in graph.Organs.ContainedEntities)
        {
            if (TryComp<OrganComponent>(candidate, out var node) && node.Category == category)
            {
                organ = candidate;
                return true;
            }
        }
        return false;
    }

    private Solution CaptureBorgSolution(EntityUid entity, string name)
        => _borgSolutions.TryGetSolution(entity, name, out _, out var solution) ? solution.Clone() : new Solution();

    private bool ValidateBorgCore(EntityUid body, NCBorgCoreSnapshot? snapshot)
    {
        if (!HasComp<NCBorgCoreComponent>(body))
            return snapshot == null;
        return snapshot != null && _borg.CanRestoreCoreIdentity(body, snapshot.CoreId) &&
               Finite(snapshot.Temperature, 273.15f, 350f) &&
               snapshot.EmpRemaining >= TimeSpan.Zero && snapshot.EmpRemaining <= TimeSpan.FromDays(3650) &&
               snapshot.PowerBus != null && _cyberware.ValidatePhysicalPowerBus(snapshot.PowerBus, snapshot.CoreId) &&
               ValidateBorgSolution(snapshot.Support, 300) && ValidateBorgSolution(snapshot.Bloodstream, 1200) &&
               ValidateBorgSolution(snapshot.Metabolites, 1200) && ValidateBorgSolution(snapshot.Stomach, 150) &&
               snapshot.ConsumptionRemainders != null && snapshot.ConsumptionRemainders.Count <= 4 &&
               snapshot.ConsumptionRemainders.All(entry =>
                   (entry.Key == NCBorgSystem.Perfusate || entry.Key == NCBorgSystem.Oxygen ||
                    entry.Key == NCBorgSystem.Dialysate || entry.Key == NCBorgSystem.Coolant) &&
                   Finite(entry.Value, 0f, 0.01f));
    }

    private bool ValidateBorgSolution(Solution? solution, float maximum)
    {
        return solution != null && solution.Contents != null && solution.Contents.Count <= 64 &&
               solution.Volume >= FixedPoint2.Zero && solution.Volume.Float() <= maximum &&
               solution.Contents.All(entry => entry.Quantity > FixedPoint2.Zero && entry.Quantity.Float() <= maximum) &&
               solution.Contents.Aggregate(FixedPoint2.Zero, (total, entry) => total + entry.Quantity) == solution.Volume &&
               solution.Contents.Select(entry => entry.Reagent).Distinct().Count() == solution.Contents.Count &&
               solution.Contents.All(entry => entry.Quantity > FixedPoint2.Zero &&
                   (entry.Reagent.Data == null || entry.Reagent.Data.Count <= 1 &&
                    entry.Reagent.Data.All(data => data is DnaData dna && dna.DNA.Length <= 128)) &&
                   _prototypes.HasIndex<ReagentPrototype>(entry.Reagent.Prototype));
    }

    private bool TryRestoreBorgCore(EntityUid body, NCBorgCoreSnapshot? snapshot)
    {
        if (!TryComp<NCBorgCoreComponent>(body, out var core))
            return snapshot == null;
        if (snapshot == null || !_borg.TryRestoreCoreIdentity((body, core), snapshot.CoreId) ||
            !_cyberware.TryRestorePowerBus(body, snapshot.PowerBus))
            return false;
        core.Occupied = snapshot.Occupied;
        core.Temperature = snapshot.Temperature;
        core.EmpDisabledUntil = _timing.CurTime + snapshot.EmpRemaining;
        core.ConsumptionRemainders = new(snapshot.ConsumptionRemainders);
        core.LastAdvancedAt = _timing.CurTime;
        RestoreBorgSolution(body, NCBorgSystem.SupportSolution, snapshot.Support);
        RestoreBorgSolution(body, "bloodstream", snapshot.Bloodstream);
        RestoreBorgSolution(body, "metabolites", snapshot.Metabolites);
        if (TryGetBorgOrgan(body, "Stomach", out var stomach))
            RestoreBorgSolution(stomach, "stomach", snapshot.Stomach);
        _borg.RefreshSupport((body, core));
        RemComp<NCBorgPendingTransferComponent>(body);
        return true;
    }

    private void RestoreBorgSolution(EntityUid entity, string name, Solution saved)
    {
        if (!_borgSolutions.TryGetSolution(entity, name, out var solution, out _))
            return;
        _borgSolutions.RemoveAllSolution(solution.Value);
        _borgSolutions.TryAddSolution(solution.Value, saved.Clone());
    }

    private static NCBorgCoreSnapshot? CloneBorgCore(NCBorgCoreSnapshot? source)
        => source == null ? null : new NCBorgCoreSnapshot
        {
            CoreId = source.CoreId,
            Occupied = source.Occupied,
            Temperature = source.Temperature,
            EmpRemaining = source.EmpRemaining,
            PowerBus = new NCCyberwarePowerBusSnapshot
            {
                Main = ClonePowerCell(source.PowerBus.Main),
                Reserve = ClonePowerCell(source.PowerBus.Reserve),
            },
            Support = source.Support.Clone(),
            Bloodstream = source.Bloodstream.Clone(),
            Metabolites = source.Metabolites.Clone(),
            Stomach = source.Stomach.Clone(),
            ConsumptionRemainders = new(source.ConsumptionRemainders),
        };

    /// <summary>Reconnect creates the saved carrier, rather than fitting a Borg graph into a fresh organic body.</summary>
    private bool TryRestorePlayerBorg(EntityUid original, NCMedicalStoredSnapshotData stored)
    {
        if (!TryDecodeStoredSnapshot(stored, out var decoded) || decoded.BorgCore == null ||
            !_borgMind.TryGetMind(original, out var mindId, out var mind))
            return false;
        var carrier = Spawn("NCBorgCore", Transform(original).Coordinates);
        if (TryComp<NCMedicalPersistenceOwnerComponent>(original, out var owner))
            BindOwner(carrier, owner.Kind, owner.Key, owner.Revision);
        if (!TryRestoreStoredMedicalState(carrier, stored))
        {
            QueueDel(carrier);
            return false;
        }
        // Destination's provisional owner marker is replaced by the exact current binding below.
        RemComp<NCMedicalPersistenceOwnerComponent>(carrier);
        _loadingBodies.Remove(original);
        if (!TryMovePlayerBinding(original, carrier))
        {
            QueueDel(carrier);
            return false;
        }
        _borgMeta.SetEntityName(carrier, Name(original));
        _borgMind.TransferTo(mindId, carrier, mind: mind);
        QueueDel(original);
        return true;
    }

    private bool TryGetLiveBorgCharacter(NCCharacterId character, out EntityUid core)
    {
        if (_borgCharacters.TryGetValue(character, out core) && Exists(core) && !TerminatingOrDeleted(core))
            return true;
        _borgCharacters.Remove(character);
        return false;
    }

    private bool TryReclaimBorgCharacter(EntityUid freshBody, EntityUid core, NCCharacterId character)
    {
        if (!_borgMind.TryGetMind(freshBody, out var mindId, out var mind) ||
            _borgMind.TryGetMind(core, out var currentMind, out _) && currentMind != mindId ||
            !TryComp<NCMedicalPersistenceOwnerComponent>(core, out var owner) || owner.SaveBlocked)
            return false;
        var state = _saveStates.GetValueOrDefault(character) ?? new OwnerSaveState { ExpectedRevision = owner.Revision };
        _saveStates[character] = state;
        _playerBindings[core] = new PlayerBinding(character, state);
        _boundCharacters[character] = core;
        _borgMind.TransferTo(mindId, core, mind: mind);
        // This is only the newly spawned placeholder; the live carrier and all online medical state are reused.
        QueueDel(freshBody);
        return true;
    }

    private void OnBorgBeforeSpawn(PlayerBeforeSpawnEvent args)
    {
        if (args.Handled || !_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var character) ||
            !TryGetLiveBorgCharacter(character, out var core))
            return;
        // The ticker must not manufacture a replacement body or mind while this character physically exists.
        args.Handled = true;
        if (!_borgMind.TryGetMind(core, out var mindId, out var mind) ||
            mind.UserId is { } user && user != args.Player.UserId ||
            !TryComp<NCMedicalPersistenceOwnerComponent>(core, out var owner) || owner.SaveBlocked)
        {
            Log.Warning($"Refused a second spawn for live Borg character {character.Value}; its existing mind needs recovery.");
            return;
        }
        _borgMind.SetUserId(mindId, args.Player.UserId, mind);
        _borgMind.UnVisit(mindId, mind);
        _borgPlayers.SetAttachedEntity(args.Player, core);
    }
}
