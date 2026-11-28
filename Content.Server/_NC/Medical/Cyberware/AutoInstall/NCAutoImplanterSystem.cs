// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server.Administration.Logs;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Arms;
using Content.Shared._NC.Medical.Cyberware.AutoImplanter;
using Content.Shared._NC.Medical.Cyberware.Neural;
using Content.Shared.Database;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware.AutoInstall;

/// <summary>Serves the event auto-implanter: lists everything installable and fits the chosen item into the user.</summary>
public sealed partial class NCAutoImplanterSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCCyberwareAutoInstallSystem _install = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAutoImplanterComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCAutoImplanterComponent, NCAutoImplanterInstallMessage>(OnInstall);
    }

    private void OnOpened(Entity<NCAutoImplanterComponent> ent, ref BoundUIOpenedEvent args)
    {
        var entries = new List<NCAutoImplanterEntry>();
        foreach (var (item, profile) in _install.GetInstallableItems())
            entries.Add(new NCAutoImplanterEntry(item, GroupOf(item, profile)));

        _ui.SetUiState(ent.Owner, NCAutoImplanterUiKey.Key, new NCAutoImplanterUiState(entries));
    }

    private void OnInstall(Entity<NCAutoImplanterComponent> ent, ref NCAutoImplanterInstallMessage args)
    {
        TryInstall(ent, args.Actor, args.Item);
    }

    /// <summary>Fits <paramref name="item"/> into <paramref name="user"/> if the implanter offers it.</summary>
    public bool TryInstall(Entity<NCAutoImplanterComponent> implanter, EntityUid user, string item)
    {
        if (!CanInstall(item))
            return false;

        if (!_install.TryAutoInstall(user, item, out var failure))
        {
            _popup.PopupEntity(Loc.GetString(failure), implanter, user, PopupType.MediumCaution);
            return false;
        }

        var name = _prototypes.Index<EntityPrototype>(item).Name;
        _popup.PopupEntity(Loc.GetString("nc-auto-implant-done", ("implant", name)), implanter, user, PopupType.Medium);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(user):user} got {item} from the auto-implanter {ToPrettyString(implanter):implanter}");
        return true;
    }

    public bool CanInstall(string item)
    {
        foreach (var (candidate, _) in _install.GetInstallableItems())
        {
            if (candidate.Id == item)
                return true;
        }

        return false;
    }

    private NCAutoImplanterGroup GroupOf(EntProtoId item, NCCyberwarePrototype profile)
    {
        var prototype = _prototypes.Index(item);
        if (profile.Form == NCCyberwareForm.LimbReplacement)
            return NCAutoImplanterGroup.Limbs;
        if (prototype.TryGetComponent<NCArmModuleComponent>(out _, _factory))
            return NCAutoImplanterGroup.Arms;
        if (prototype.TryGetComponent<NCNeuralHardwareComponent>(out _, _factory) || profile.RequiredNeuralInterface != null)
            return NCAutoImplanterGroup.Neural;
        if (profile.SkeletalEnhancement || profile.DermalArmor)
            return NCAutoImplanterGroup.Frame;
        if (item.Id.Contains("Eye", StringComparison.Ordinal) || item.Id.Contains("Optic", StringComparison.Ordinal))
            return NCAutoImplanterGroup.Optics;
        return NCAutoImplanterGroup.Internal;
    }
}
