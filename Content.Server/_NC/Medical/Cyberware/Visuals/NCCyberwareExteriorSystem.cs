// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Visuals;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Interaction;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.ActionBlocker;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.Server._NC.Medical.Cyberware.Visuals;

/// <summary>Only visible exterior data enters native VisualOrgan state. No internal identity or diagnostics are replicated.</summary>
public sealed partial class NCCyberwareExteriorSystem : EntitySystem
{
    [Dependency] private SharedVisualBodySystem _visuals = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private NCMedicalBodySystem _medical = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCyberwareExteriorComponent, NCGetOrganVisualAppearanceEvent>(OnAppearance);
        SubscribeLocalEvent<NCCyberwareExteriorComponent, MapInitEvent>(OnExteriorInit,
            after: [typeof(InitialBodySystem), typeof(NCCyberwareSystem)]);
        SubscribeLocalEvent<NCCyberwareExteriorBodyComponent, MapInitEvent>(OnBodyInit,
            after: [typeof(InitialBodySystem), typeof(NCCyberwareSystem)]);
        SubscribeLocalEvent<NCCyberwareExteriorBodyComponent, NCMedicalBodyChangedEvent>(OnBodyChanged);
        SubscribeLocalEvent<NCCyberwareFinishApplicatorComponent, AfterInteractEvent>(OnApplyFinish);
    }

    private void OnAppearance(Entity<NCCyberwareExteriorComponent> ent, ref NCGetOrganVisualAppearanceEvent args)
    {
        if (!_prototypes.TryIndex(ent.Comp.Finish, out var finish) ||
            !TryComp<VisualOrganComponent>(ent, out var visual) || args.Data.State == null)
            return;
        // Both finishes use exactly the native anatomical layer. Clothes and organ removal keep their normal behavior.
        // Appearance copying can pass another organ's data object. Never repaint that source organ by aliasing it.
        args.Data = _serialization.CreateCopy(args.Data, notNullableOverride: true);
        var skin = finish.SyntheticSkin && !ent.Comp.Ruptured;
        args.Data.RsiPath = skin ? "Mobs/Species/Human/parts.rsi" : "_NC/Mobs/Cyberware/parts.rsi";
        args.Data.TexturePath = null;
        args.Data.Color = skin ? ent.Comp.SkinColor ?? visual.Profile.SkinColor : ent.Comp.Ruptured ? Color.FromHex("#B87872") : finish.Color;
        args.Data.Visible = true;
    }

    private void OnExteriorInit(Entity<NCCyberwareExteriorComponent> ent, ref MapInitEvent args) => RefreshExterior(ent);
    private void OnBodyInit(Entity<NCCyberwareExteriorBodyComponent> ent, ref MapInitEvent args) => RefreshBody(ent);
    private void OnBodyChanged(Entity<NCCyberwareExteriorBodyComponent> ent, ref NCMedicalBodyChangedEvent args) => RefreshBody(ent);

    private void RefreshBody(EntityUid body)
    {
        if (!TryComp<BodyComponent>(body, out var graph) || graph.Organs == null) return;
        foreach (var uid in graph.Organs.ContainedEntities)
            if (!TerminatingOrDeleted(uid) && TryComp<NCCyberwareExteriorComponent>(uid, out var exterior))
                RefreshExterior((uid, exterior));
    }

    private void RefreshExterior(Entity<NCCyberwareExteriorComponent> ent)
    {
        var wasRuptured = ent.Comp.Ruptured;
        if (TryComp<NCOrganIntegrityComponent>(ent, out var integrity))
        {
            if (ent.Comp.ObservedIntegrity is { } previous && integrity.CurrentIntegrity < previous &&
                integrity.CurrentIntegrity < integrity.MaxIntegrity * 0.7f)
                ent.Comp.Ruptured = true;
            ent.Comp.ObservedIntegrity = integrity.CurrentIntegrity;
        }
        if (ent.Comp.ProjectionInitialized && ent.Comp.ProjectedFinish == ent.Comp.Finish &&
            ent.Comp.ProjectedRuptured == ent.Comp.Ruptured) return;
        ent.Comp.ProjectionInitialized = true;
        ent.Comp.ProjectedFinish = ent.Comp.Finish;
        ent.Comp.ProjectedRuptured = ent.Comp.Ruptured;
        if (HasComp<VisualOrganComponent>(ent))
            _visuals.TryRefreshOrganAppearance(ent.Owner);
        if (wasRuptured != ent.Comp.Ruptured && TryComp<OrganComponent>(ent, out var organ) && organ.Body is { } body)
            _medical.TryMarkDirty(body, NCMedicalDirtyDomains.Persistence);
    }

    private void OnApplyFinish(Entity<NCCyberwareFinishApplicatorComponent> kit, ref AfterInteractEvent args)
    {
        if (!args.Handled && args.CanReach && args.Target is { } target)
            args.Handled = TryApplyFinish(args.User, (kit.Owner, kit.Comp), target);
    }

    /// <summary>One real consumable coats a loose anatomical part. It restores no mechanical integrity or resources.</summary>
    public bool TryApplyFinish(EntityUid user, Entity<NCCyberwareFinishApplicatorComponent?> kit, EntityUid target)
    {
        if (!Resolve(kit, ref kit.Comp, false) || !CanApplyFinish(user, kit, target)) return false;
        kit.Comp!.Consumed = true;
        var exterior = Comp<NCCyberwareExteriorComponent>(target);
        exterior.Finish = kit.Comp!.Finish;
        exterior.Ruptured = false;
        if (TryComp<VisualOrganComponent>(target, out var visual)) exterior.SkinColor = visual.Profile.SkinColor;
        exterior.ProjectionInitialized = false;
        RefreshExterior((target, exterior));
        _popup.PopupEntity(Loc.GetString("nc-cyberware-finish-applied"), target, user);
        QueueDel(kit);
        return true;
    }

    public bool CanApplyFinish(EntityUid user, Entity<NCCyberwareFinishApplicatorComponent?> kit, EntityUid target)
    {
        return Resolve(kit, ref kit.Comp, false) && !kit.Comp.Consumed && !TerminatingOrDeleted(kit) &&
            _blocker.CanInteract(user, target) &&
            !TerminatingOrDeleted(target) && _hands.IsHolding(user, kit) &&
            _interaction.InRangeUnobstructed(user, target) &&
            _prototypes.HasIndex(kit.Comp.Finish) && HasComp<NCCyberwareExteriorComponent>(target) &&
            TryComp<OrganComponent>(target, out var organ) && organ.Body == null;
    }
}
