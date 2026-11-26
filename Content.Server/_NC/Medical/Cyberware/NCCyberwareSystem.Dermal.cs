// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Combat.Armor;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.FixedPoint;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private NCArmorCoverageSystem _dermalCoverage = default!;

    private void InitializeDermalEvents()
    {
        SubscribeLocalEvent<NCCyberwareComponent, NCArmorAblatedEvent>(OnDermalAblated);
    }

    private void RefreshDermalArmor(EntityUid uid, NCCyberwareComponent module, NCCyberwarePrototype profile)
    {
        if (!profile.DermalArmor || !TryComp<NCArmorComponent>(uid, out var armor) ||
            !TryComp<NCOrganIntegrityComponent>(uid, out var integrity)) return;
        foreach (var section in armor.Sections)
        {
            var sp = FixedPoint2.New(NCDermalArmorMath.Sp(section.MaxSp.Float(), integrity.CurrentIntegrity, profile.MaximumIntegrity));
            if (section.CurrentSp == sp) continue;
            section.CurrentSp = sp;
            unchecked { armor.Revision++; }
        }
        if (module.InstalledSlot != null && module.Operational && TryResolveOwningBody(uid, out var body))
            _dermalCoverage.TryBindSource(uid, body);
        else
            _dermalCoverage.UnbindSource(uid);
    }

    private void OnDermalAblated(EntityUid implant, NCCyberwareComponent module, NCArmorAblatedEvent args)
    {
        if (!TryGetProfile(module.Profile, out var profile) || !profile.DermalArmor ||
            !TryComp<NCOrganIntegrityComponent>(implant, out var integrity) ||
            !_prototypes.TryIndex(args.Section, out var section)) return;
        integrity.CurrentIntegrity = NCDermalArmorMath.Ablate(integrity.CurrentIntegrity, profile.MaximumIntegrity,
            section.MaxSp.Float(), (args.PreviousSp - args.CurrentSp).Float());
        module.FactoryPrepared = false;
        UpdateOperational(implant, module, profile, _timing.CurTime);
        RefreshFactoryAppearance(implant, module);
        if (TryResolveOwningBody(implant, out var body)) MarkBodyChanged(body);
    }
}
