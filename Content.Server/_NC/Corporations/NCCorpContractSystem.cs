// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Shared._NC.Bank;
using Content.Shared._NC.Corporations;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._NC.Corporations;

/// <summary>
/// Corporate sales contracts. Each round every corporation draws its open contracts from the prototype pool; a
/// contract is fulfilled in full from the goods on the sales pallets next to the console, which pays the reward
/// split between the field department and the corporation account and draws a new contract into the slot.
/// </summary>
public sealed partial class NCCorpContractSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCBankSystem _bank = default!;

    private readonly Dictionary<string, List<ProtoId<NCCorpContractPrototype>?>> _open = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCorpSalesConsoleComponent, BoundUIOpenedEvent>((ent, ref _) => UpdateUi(ent));
        SubscribeLocalEvent<NCCorpSalesConsoleComponent, NCCorpSalesRefreshMessage>((ent, ref _) => UpdateUi(ent));
        SubscribeLocalEvent<NCCorpSalesConsoleComponent, NCCorpSalesFulfillMessage>(OnFulfill);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _open.Clear());
    }

    #region Contract pool

    /// <summary>The open contract slots of a corporation this round; empty slots are null.</summary>
    public IReadOnlyList<ProtoId<NCCorpContractPrototype>?> GetOpenContracts(ProtoId<NCCorpSalesPrototype> corporation)
    {
        if (_open.TryGetValue(corporation, out var slots))
            return slots;

        slots = new List<ProtoId<NCCorpContractPrototype>?>();
        _open[corporation] = slots;
        var count = _prototypes.TryIndex(corporation, out var prototype) ? prototype.OpenContracts : 0;
        for (var i = 0; i < count; i++)
            slots.Add(Draw(corporation, slots));

        return slots;
    }

    /// <summary>Puts a specific contract of the corporation into an open slot, replacing what was there.</summary>
    public bool TrySetOpenContract(ProtoId<NCCorpSalesPrototype> corporation, int slot, ProtoId<NCCorpContractPrototype> contract)
    {
        GetOpenContracts(corporation);
        if (!_open.TryGetValue(corporation, out var slots) || slot < 0 || slot >= slots.Count ||
            !_prototypes.TryIndex(contract, out var prototype) || prototype.Corporation != corporation)
        {
            return false;
        }

        slots[slot] = contract;
        return true;
    }

    /// <summary>Draws a weighted random contract of the corporation that is not open already.</summary>
    private ProtoId<NCCorpContractPrototype>? Draw(string corporation, List<ProtoId<NCCorpContractPrototype>?> open)
    {
        var pool = _prototypes.EnumeratePrototypes<NCCorpContractPrototype>()
            .Where(contract => contract.Corporation == corporation && contract.Weight > 0 &&
                               !open.Contains(contract.ID))
            .ToList();
        if (pool.Count == 0)
            return null;

        var roll = _random.NextFloat() * pool.Sum(contract => contract.Weight);
        foreach (var contract in pool)
        {
            roll -= contract.Weight;
            if (roll <= 0)
                return contract.ID;
        }

        return pool[^1].ID;
    }

    #endregion

    #region Goods

    /// <summary>Sales pallets that serve a console.</summary>
    public HashSet<Entity<NCCorpSalesPalletComponent>> GetPallets(Entity<NCCorpSalesConsoleComponent> console)
    {
        return _lookup.GetEntitiesInRange<NCCorpSalesPalletComponent>(
            Transform(console).Coordinates,
            console.Comp.PalletRange);
    }

    /// <summary>Everything lying on the pallets, including the contents of crates and boxes, but never mobs.</summary>
    public List<EntityUid> GetGoods(IEnumerable<Entity<NCCorpSalesPalletComponent>> pallets)
    {
        var goods = new List<EntityUid>();
        var seen = new HashSet<EntityUid>();
        foreach (var pallet in pallets)
        {
            foreach (var entity in _lookup.GetEntitiesIntersecting(pallet, LookupFlags.Dynamic | LookupFlags.Sundries))
            {
                if (entity != pallet.Owner)
                    Collect(entity, goods, seen);
            }
        }

        return goods;
    }

    private void Collect(EntityUid entity, List<EntityUid> goods, HashSet<EntityUid> seen)
    {
        if (!seen.Add(entity) || HasComp<MobStateComponent>(entity) || Transform(entity).Anchored)
            return;

        goods.Add(entity);
        if (!TryComp<ContainerManagerComponent>(entity, out var manager))
            return;

        foreach (var container in _containers.GetAllContainers(entity, manager))
        {
            foreach (var contained in container.ContainedEntities)
                Collect(contained, goods, seen);
        }
    }

    /// <summary>How many units of the requirement one entity provides.</summary>
    private int Units(NCCorpContractRequirement requirement, EntityUid entity)
    {
        switch (requirement)
        {
            case NCCorpItemRequirement item:
                if (MetaData(entity).EntityPrototype?.ID != item.Item.Id)
                    return 0;
                return TryComp<StackComponent>(entity, out var stack) ? stack.Count : 1;
            case NCCorpImplantRequirement implant:
                return IsFinishedImplant(entity, implant) ? 1 : 0;
            case NCCorpDrugRequirement drug:
                return TryComp<NCPharmaContainerComponent>(entity, out var container) &&
                       container.FormulaId == drug.Formula.Id &&
                       container.LabelGrade >= drug.MinimumGrade &&
                       (!drug.Certified || container.QcState == NCPharmaQcState.Certified)
                    ? Math.Max(0, container.Doses)
                    : 0;
            default:
                return 0;
        }
    }

    private bool IsFinishedImplant(EntityUid entity, NCCorpImplantRequirement implant)
    {
        if (MetaData(entity).EntityPrototype is not { } prototype)
            return false;

        if (prototype.ID != implant.Item.Id &&
            !_prototypes.EnumerateParents<EntityPrototype>(prototype.ID).Any(parent => parent.ID == implant.Item.Id))
        {
            return false;
        }

        if (!TryComp<NCCyberwareAssemblyComponent>(entity, out var assembly))
            return true;

        return assembly.FunctionalEfficiency > 0f &&
               (!implant.Tested || assembly.ValidatedRevision == assembly.ConfigurationRevision);
    }

    /// <summary>
    /// Matches the requirements against the goods. Each entity serves one requirement at most. Returns the
    /// progress of every requirement and the entities that would be taken for each.
    /// </summary>
    private List<(NCCorpRequirementProgress Progress, List<(EntityUid Entity, int Units)> Taken)> Match(
        NCCorpContractPrototype contract,
        List<EntityUid> goods)
    {
        return Match(contract.Requirements, goods);
    }

    /// <inheritdoc cref="Match(NCCorpContractPrototype, List{EntityUid})"/>
    public List<(NCCorpRequirementProgress Progress, List<(EntityUid Entity, int Units)> Taken)> Match(
        List<NCCorpContractRequirement> requirements,
        List<EntityUid> goods)
    {
        var used = new HashSet<EntityUid>();
        var result = new List<(NCCorpRequirementProgress, List<(EntityUid, int)>)>();
        for (var i = 0; i < requirements.Count; i++)
        {
            var requirement = requirements[i];
            var taken = new List<(EntityUid, int)>();
            var have = 0;
            foreach (var entity in goods)
            {
                if (have >= requirement.Amount || used.Contains(entity))
                    continue;

                var units = Units(requirement, entity);
                if (units <= 0)
                    continue;

                used.Add(entity);
                var take = requirement is NCCorpItemRequirement ? Math.Min(units, requirement.Amount - have) : units;
                taken.Add((entity, take));
                have += take;
            }

            result.Add((new NCCorpRequirementProgress(i, have, requirement.Amount), taken));
        }

        return result;
    }

    /// <summary>Takes the matched goods: partial stacks shrink, everything else is deleted.</summary>
    public void Consume(List<(NCCorpRequirementProgress Progress, List<(EntityUid Entity, int Units)> Taken)> match)
    {
        foreach (var (_, taken) in match)
        {
            foreach (var (entity, units) in taken)
            {
                if (TryComp<StackComponent>(entity, out var stack) && stack.Count > units)
                    _stacks.SetCount((entity, stack), stack.Count - units);
                else
                    QueueDel(entity);
            }
        }
    }

    /// <summary>An entity's contents, recursively through containers, never including mobs.</summary>
    public List<EntityUid> GetContents(EntityUid root)
    {
        var goods = new List<EntityUid>();
        var seen = new HashSet<EntityUid> { root };
        if (!TryComp<ContainerManagerComponent>(root, out var manager))
            return goods;

        foreach (var container in _containers.GetAllContainers(root, manager))
        {
            foreach (var contained in container.ContainedEntities)
                Collect(contained, goods, seen);
        }

        return goods;
    }

    #endregion

    #region Fulfilment

    public bool IsEmployee(EntityUid user, ProtoId<NCCorpSalesPrototype> corporation)
    {
        return _prototypes.TryIndex(corporation, out var sales) &&
               _prototypes.TryIndex(sales.Department, out var bankDepartment) &&
               bankDepartment.Department is { } departmentId &&
               _prototypes.TryIndex(departmentId, out var department) &&
               _minds.TryGetMind(user, out var mindId, out _) &&
               _jobs.MindTryGetJobId(mindId, out var job) &&
               job is { } jobId &&
               department.Roles.Contains(jobId);
    }

    private void OnFulfill(Entity<NCCorpSalesConsoleComponent> ent, ref NCCorpSalesFulfillMessage args)
    {
        TryFulfill(ent, args.Slot, args.Actor);
        UpdateUi(ent);
    }

    /// <summary>Fulfils an open contract in full from the console's pallets.</summary>
    public bool TryFulfill(Entity<NCCorpSalesConsoleComponent> console, int slot, EntityUid user)
    {
        var corporation = console.Comp.Corporation;
        var slots = GetOpenContracts(corporation);
        if (!IsEmployee(user, corporation))
        {
            _popup.PopupEntity(Loc.GetString("nc-corp-sales-not-employee"), console, user);
            return false;
        }

        if (slot < 0 || slot >= slots.Count || slots[slot] is not { } contractId ||
            !_prototypes.TryIndex(contractId, out var contract) ||
            !_prototypes.TryIndex(corporation, out var sales))
        {
            return false;
        }

        var match = Match(contract, GetGoods(GetPallets(console)));
        if (!match.TrueForAll(entry => entry.Progress.Have >= entry.Progress.Need))
        {
            _popup.PopupEntity(Loc.GetString("nc-corp-sales-incomplete"), console, user);
            return false;
        }

        if (!_bank.TryPayContract(sales.Department, sales.Corporation, contract.Reward, sales.DepartmentShare, contract.ID, user))
        {
            _popup.PopupEntity(Loc.GetString("nc-bank-offline"), console, user);
            return false;
        }

        Consume(match);

        if (_open.TryGetValue(corporation, out var open))
            open[slot] = Draw(corporation, open);

        _popup.PopupEntity(Loc.GetString("nc-corp-sales-fulfilled",
            ("contract", Loc.GetString(contract.Name)),
            ("reward", contract.Reward)), console, user);
        return true;
    }

    #endregion

    private void UpdateUi(Entity<NCCorpSalesConsoleComponent> ent)
    {
        var actor = _ui.GetActors(ent.Owner, NCCorpSalesUiKey.Key).Select(uid => (EntityUid?) uid).FirstOrDefault();
        var pallets = GetPallets(ent);
        var goods = GetGoods(pallets);
        var entries = new List<NCCorpContractEntry>();
        var slots = GetOpenContracts(ent.Comp.Corporation);
        for (var slot = 0; slot < slots.Count; slot++)
        {
            if (slots[slot] is not { } contractId || !_prototypes.TryIndex(contractId, out var contract))
                continue;

            entries.Add(new NCCorpContractEntry(slot, contractId, Match(contract, goods).Select(entry => entry.Progress).ToList()));
        }

        _ui.SetUiState(ent.Owner, NCCorpSalesUiKey.Key, new NCCorpSalesUiState(
            ent.Comp.Corporation,
            actor != null && IsEmployee(actor.Value, ent.Comp.Corporation),
            pallets.Count > 0,
            entries));
    }
}
