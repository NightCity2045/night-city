// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Arms;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware.AutoInstall;

/// <summary>
/// Installs a piece of cyberware in one step, the way a finished clinic job would leave it: the hardware it depends
/// on (power bus and cell, neural foundation, skeletal support grafts, a cyberarm for arm modules) is fitted first,
/// skeletal anchoring is completed and the implant is calibrated. Used by the event auto-implanter and role presets.
/// </summary>
public sealed partial class NCCyberwareAutoInstallSystem : EntitySystem
{
    private const int MaximumDepth = 4;
    private const int MaximumModules = 64;
    private static readonly EntProtoId PowerBus = "NCImplantPowerBusMk1";
    private static readonly EntProtoId PowerCell = "NCCyberwarePowerCell";
    private static readonly EntProtoId PowerDrive = "NCArmPowerDriveStandard";
    private static readonly EntProtoId[] CivilianArms = ["NCOrganHumanCivilianCyberArmRight", "NCOrganHumanCivilianCyberArmLeft"];
    private static readonly EntProtoId[] HeavyArms = ["NCOrganHumanArmoredCyberArmRight", "NCOrganHumanArmoredCyberArmLeft"];

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCCyberwareAbilitySystem _abilities = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCOrganDetachmentSystem _detachment = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedBatterySystem _battery = default!;
    [Dependency] private SharedContainerSystem _containers = default!;

    private readonly Dictionary<ProtoId<NCCyberwarePrototype>, EntProtoId> _itemByProfile = new();
    private readonly List<(EntProtoId Item, NCCyberwarePrototype Profile)> _catalog = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        IndexItems();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<EntityPrototype>() || args.WasModified<NCCyberwarePrototype>())
            IndexItems();
    }

    private void IndexItems()
    {
        _itemByProfile.Clear();
        _catalog.Clear();
        var hosted = new HashSet<ProtoId<NCCyberwareSlotPrototype>>();
        var ready = new List<(EntityPrototype Prototype, NCCyberwarePrototype Profile)>();
        foreach (var prototype in _prototypes.EnumeratePrototypes<EntityPrototype>().OrderBy(p => p.ID, StringComparer.Ordinal))
        {
            if (prototype.Abstract)
                continue;

            if (prototype.TryGetComponent<NCCyberwareSlotHostComponent>(out var host, _factory))
                hosted.UnionWith(host.Slots);

            // Unassembled frames start empty and are not something a clinic would fit.
            if (prototype.TryGetComponent<NCCyberwareAssemblyComponent>(out var parts, _factory) && parts.Initialized ||
                !prototype.TryGetComponent<NCCyberwareComponent>(out var module, _factory) ||
                !_prototypes.TryIndex(module.Profile, out var profile) ||
                !module.FactoryPrepared && profile.RequiresFactoryPreparation)
            {
                continue;
            }

            _itemByProfile.TryAdd(module.Profile, prototype.ID);
            ready.Add((prototype, profile));
        }

        // Borg carriers are a separate body, and an implant whose bay no organ carries cannot be fitted at all.
        foreach (var (prototype, profile) in ready)
        {
            if (prototype.ID.StartsWith("NCBorg", StringComparison.Ordinal) ||
                profile.ID.StartsWith("NCBorg", StringComparison.Ordinal) ||
                profile.Form == NCCyberwareForm.Implant && !profile.CompatibleSlots.Any(hosted.Contains))
            {
                continue;
            }

            _catalog.Add((prototype.ID, profile));
        }
    }

    /// <summary>The ready-to-install item for a profile, if one exists.</summary>
    public bool TryGetItem(ProtoId<NCCyberwarePrototype> profile, out EntProtoId item)
    {
        return _itemByProfile.TryGetValue(profile, out item);
    }

    /// <summary>Every cyberware item the auto-installer can fit, excluding Borg carriers.</summary>
    public IReadOnlyList<(EntProtoId Item, NCCyberwarePrototype Profile)> GetInstallableItems()
    {
        return _catalog;
    }

    /// <summary>Installs <paramref name="item"/> into <paramref name="body"/> with everything it needs to work.</summary>
    public bool TryAutoInstall(EntityUid body, EntProtoId item, [NotNullWhen(false)] out string? failure)
    {
        failure = null;
        if (!HasComp<NCMedicalBodyComponent>(body) || !HasComp<BodyComponent>(body))
        {
            failure = "nc-auto-implant-fail-body";
            return false;
        }

        if (!_prototypes.TryIndex(item, out var prototype) ||
            !prototype.TryGetComponent<NCCyberwareComponent>(out var template, _factory) ||
            !_prototypes.TryIndex(template.Profile, out var profile))
        {
            failure = "nc-auto-implant-fail-unknown";
            return false;
        }

        var installed = Install(body, item, profile, 0, out failure);
        _abilities.RefreshBody(body);
        return installed;
    }

    /// <summary>True if a module with this profile is already installed in the body.</summary>
    public bool HasInstalled(EntityUid body, ProtoId<NCCyberwarePrototype> profile)
    {
        return CountInstalled(body, profile) > 0;
    }

    private int CountInstalled(EntityUid body, ProtoId<NCCyberwarePrototype> profile)
    {
        var count = 0;
        foreach (var uid in _cyberware.GetInstalledModuleEntities(body, MaximumModules))
        {
            if (TryComp<NCCyberwareComponent>(uid, out var module) && module.Profile == profile)
                count++;
        }

        return count;
    }

    private bool Install(EntityUid body, EntProtoId item, NCCyberwarePrototype profile, int depth, out string? failure)
    {
        failure = "nc-auto-implant-fail-dependencies";
        if (depth > MaximumDepth || !SatisfyDependencies(body, profile, depth, out failure))
            return false;

        failure = null;
        return profile.Form == NCCyberwareForm.Implant
            ? InstallImplant(body, item, depth, out failure)
            : ReplaceOrgan(body, item, out failure);
    }

    private bool SatisfyDependencies(EntityUid body, NCCyberwarePrototype profile, int depth, out string? failure)
    {
        failure = null;
        if (profile.RequiresPower && !HasPowerBus(body) && !InstallPowerBus(body, out failure))
            return false;

        if (profile.RequiredNeuralInterface is { } neural && !HasInstalled(body, neural) &&
            !InstallProfile(body, neural, depth, out failure))
        {
            return false;
        }

        if (profile.RequiredSupportProfile is { } support)
        {
            for (var i = CountInstalled(body, support); i < Math.Max(1, profile.RequiredSupportCount); i++)
            {
                if (!InstallProfile(body, support, depth, out failure))
                    return false;
            }
        }

        return true;
    }

    private bool InstallProfile(EntityUid body, ProtoId<NCCyberwarePrototype> profileId, int depth, out string? failure)
    {
        failure = "nc-auto-implant-fail-dependencies";
        return _itemByProfile.TryGetValue(profileId, out var item) &&
               _prototypes.TryIndex(profileId, out var profile) &&
               Install(body, item, profile, depth + 1, out failure);
    }

    private bool InstallImplant(EntityUid body, EntProtoId item, int depth, out string? failure)
    {
        var module = Spawn(item, Transform(body).Coordinates);
        if (!TryFindHost(body, module, out var host, out var slot) &&
            !(TryComp<NCArmModuleComponent>(module, out var arm) && FitCyberarm(body, arm, depth) &&
              TryFindHost(body, module, out host, out slot)))
        {
            QueueDel(module);
            failure = "nc-auto-implant-fail-slot";
            return false;
        }

        var result = _cyberware.TryInstall(new NCCyberwareInstallRequest(
            NCActionResolutionId.New(), body, host, module, slot, true, true));
        if (!result.Accepted)
        {
            QueueDel(module);
            failure = "nc-auto-implant-fail-slot";
            return false;
        }

        return Finish(module, out failure);
    }

    private bool TryFindHost(EntityUid body, EntityUid module, out EntityUid host,
        out ProtoId<NCCyberwareSlotPrototype> slot)
    {
        host = default;
        slot = default;
        if (Comp<BodyComponent>(body).Organs is not { } organs)
            return false;

        foreach (var organ in organs.ContainedEntities.OrderBy(uid => uid.Id))
        {
            if (_cyberware.TryFindInstallSlot(body, organ, module, out slot) &&
                _cyberware.CanFitArmModule(organ, module))
            {
                host = organ;
                return true;
            }
        }

        return false;
    }

    /// <summary>Arm modules ride in a cyberarm. Replace a natural arm, right side first, with a frame that can carry it.</summary>
    private bool FitCyberarm(EntityUid body, NCArmModuleComponent module, int depth)
    {
        foreach (var arm in module.RequiresHeavyFrame ? HeavyArms : CivilianArms)
        {
            if (!_prototypes.TryIndex(arm, out var prototype) ||
                !prototype.TryGetComponent<OrganComponent>(out var organ, _factory) ||
                organ.Category is not { } category ||
                !_anatomy.TryGetOrgan(body, category, out var current) ||
                HasComp<NCCyberwareComponent>(current) ||
                !prototype.TryGetComponent<NCCyberwareComponent>(out var template, _factory) ||
                !_prototypes.TryIndex(template.Profile, out var profile))
            {
                continue;
            }

            if (!Install(body, arm, profile, depth + 1, out _))
                continue;

            if (module.RequiresHeavyFrame && _anatomy.TryGetOrgan(body, category, out var fitted))
                FitPowerDrive(fitted);
            return true;
        }

        return false;
    }

    /// <summary>Heavy arm modules need more than a stock actuator: swap in a power drive and recalibrate the arm.</summary>
    private void FitPowerDrive(EntityUid arm)
    {
        if (!TryComp<NCCyberwareAssemblyComponent>(arm, out var assembly) ||
            !_prototypes.TryIndex(PowerDrive, out var prototype) ||
            !prototype.TryGetComponent<NCCyberwarePartComponent>(out var part, _factory) ||
            !_prototypes.TryIndex(part.Profile, out var partProfile) ||
            !assembly.Parts.TryGetValue(partProfile.Slot, out var slot))
        {
            return;
        }

        if (slot.ContainedEntity is { } old)
        {
            _containers.Remove(old, slot);
            QueueDel(old);
        }

        var drive = Spawn(PowerDrive, Transform(arm).Coordinates);
        if (!_containers.Insert(drive, slot))
        {
            QueueDel(drive);
            return;
        }

        assembly.ConfigurationRevision++;
        _cyberware.TryCalibrateCyberware(arm, NCActionResolutionId.New(), out _);
    }

    private bool ReplaceOrgan(EntityUid body, EntProtoId item, out string? failure)
    {
        failure = "nc-auto-implant-fail-organ";
        if (!_prototypes.TryIndex(item, out var prototype) ||
            !prototype.TryGetComponent<OrganComponent>(out var template, _factory) ||
            template.Category is not { } category ||
            !_anatomy.TryGetOrgan(body, category, out var current) ||
            !TryComp<ChildOrganComponent>(current, out var child) ||
            child.Parent is not { } parent ||
            !TryComp<OrganComponent>(parent, out var parentOrgan) ||
            parentOrgan.Category is not { } parentCategory)
        {
            return false;
        }

        if (!_detachment.TryDetachOrgan(body, category, out var removed))
            return false;

        var graft = Spawn(item, Transform(body).Coordinates);
        if (!_detachment.TryAttachOrgan(body, parentCategory, graft))
        {
            QueueDel(graft);
            _detachment.TryAttachOrgan(body, parentCategory, removed);
            return false;
        }

        DeleteSubtree(removed);
        return Finish(graft, out failure);
    }

    private void DeleteSubtree(EntityUid root)
    {
        if (TryComp<ParentOrganComponent>(root, out var parent))
        {
            foreach (var child in parent.Children.ToArray())
                DeleteSubtree(child);
        }

        QueueDel(root);
    }

    /// <summary>Anchors, calibrates and load-tests a freshly mounted module.</summary>
    private bool Finish(EntityUid module, out string? failure)
    {
        failure = null;
        var component = Comp<NCCyberwareComponent>(module);
        var profile = _prototypes.Index(component.Profile);
        foreach (var step in profile.SkeletalSurgery)
        {
            _cyberware.TryPerformSkeletalSurgery((module, component), step.Zone, step.Action,
                NCActionResolutionId.New(), out _);
        }

        // Replacement organs have no bay, so the surgical stage API refuses them; the clinic job counts as done.
        if (!NCSkeletalSurgeryPlan.Complete(profile.SkeletalSurgery, component.SkeletalSurgeryCompleted))
            component.SkeletalSurgeryCompleted |= NCSkeletalSurgeryPlan.Required(profile.SkeletalSurgery);

        if (!_cyberware.TryCalibrateCyberware((module, component), NCActionResolutionId.New(), out _) &&
            !component.Calibrated)
        {
            failure = "nc-auto-implant-fail-calibration";
            return false;
        }

        if (profile.RequiresSkeletalLoadTest && !component.SkeletalLoadTestPassed)
            _cyberware.TryTestSkeletalLoad((module, component), NCActionResolutionId.New(), out _);

        return true;
    }

    private bool HasPowerBus(EntityUid body)
    {
        return _cyberware.GetInstalledModuleEntities(body, MaximumModules).Any(HasComp<NCCyberwarePowerBusComponent>);
    }

    private bool InstallPowerBus(EntityUid body, out string? failure)
    {
        failure = "nc-auto-implant-fail-power";
        var bus = Spawn(PowerBus, Transform(body).Coordinates);
        if (!TryFindHost(body, bus, out var host, out var slot) ||
            !_cyberware.TryInstall(new NCCyberwareInstallRequest(NCActionResolutionId.New(), body, host, bus, slot))
                .Accepted)
        {
            QueueDel(bus);
            return false;
        }

        var cell = Spawn(PowerCell, Transform(body).Coordinates);
        if (TryComp<BatteryComponent>(cell, out var battery))
            _battery.SetCharge((cell, battery), battery.MaxCharge);

        _slots.SetLock(bus, NCCyberwarePowerBusComponent.MainSlot, false);
        var inserted = _slots.TryInsert(bus, NCCyberwarePowerBusComponent.MainSlot, cell, null);
        _slots.SetLock(bus, NCCyberwarePowerBusComponent.MainSlot, true);
        if (!inserted)
        {
            QueueDel(cell);
            return false;
        }

        _cyberware.ResetPowerClock(body);
        failure = null;
        return true;
    }
}
