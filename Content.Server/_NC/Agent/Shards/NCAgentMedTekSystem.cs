// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server.CartridgeLoader.Cartridges;
using Content.Server.Medical;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.Shards.MedTek;
using Content.Shared.Administration.Logs;
using Content.Shared.CartridgeLoader;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;

namespace Content.Server._NC.Agent.Shards;

/// <summary>
/// Captures completed vanilla health-analyzer scans into the installed physical MEDTEK shard.
/// </summary>
public sealed partial class NCAgentMedTekSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridges = default!;
    [Dependency] private HealthAnalyzerSystem _healthAnalyzer = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private ISharedAdminLogManager _adminLog = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAgentComponent, NCAgentMedicalScanCompletedEvent>(OnScanCompleted);
        SubscribeLocalEvent<NCAgentMedTekComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<NCAgentMedTekComponent, CartridgeMessageEvent>(OnUiMessage);
    }

    private void OnScanCompleted(Entity<NCAgentComponent> ent, ref NCAgentMedicalScanCompletedEvent args)
    {
        if (!TryComp<CartridgeLoaderComponent>(ent, out var loader) ||
            _cartridges.TryGetProgram<NCAgentMedTekComponent>((ent.Owner, loader)) is not { } medTek ||
            !TryComp<DamageableComponent>(args.Target, out var damageable))
        {
            return;
        }

        var health = _healthAnalyzer.GetHealthAnalyzerUiState(args.Target);
        var damage = _damageable.GetAllDamage((args.Target, damageable)).DamageDict
            .Where(entry => entry.Value > 0)
            .OrderByDescending(entry => entry.Value)
            .Select(entry => new NCAgentMedicalDamage(entry.Key.ToString(), entry.Value.Float()))
            .ToList();
        medTek.Comp.History.Add(new NCAgentMedicalScan(
            Guid.NewGuid(),
            Name(args.Target),
            DateTime.UtcNow,
            health.Temperature,
            health.BloodLevel,
            health.Bleeding == true,
            health.Unrevivable == true,
            _damageable.GetTotalDamage((args.Target, damageable)).Float(),
            damage));

        while (medTek.Comp.History.Count > medTek.Comp.MaximumHistory)
            medTek.Comp.History.RemoveAt(0);

        Dirty(medTek);
        _adminLog.Add(
            LogType.PdaInteract,
            LogImpact.Low,
            $"{ToPrettyString(ent)} stored a MEDTEK scan of {ToPrettyString(args.Target)}");
        UpdateUi(medTek, ent.Owner);
    }

    private void OnUiReady(Entity<NCAgentMedTekComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateUi(ent, args.Loader);
    }

    private void OnUiMessage(Entity<NCAgentMedTekComponent> ent, ref CartridgeMessageEvent args)
    {
        if (args is not NCAgentMedTekClearMessage || ent.Comp.History.Count == 0)
            return;

        ent.Comp.History.Clear();
        Dirty(ent);
        _adminLog.Add(
            LogType.PdaInteract,
            LogImpact.Low,
            $"{ToPrettyString(args.Actor)} cleared Agent MEDTEK history on {ToPrettyString(ent)}");
        UpdateUi(ent, GetEntity(args.LoaderUid));
    }

    private void UpdateUi(Entity<NCAgentMedTekComponent> ent, EntityUid loader)
    {
        _cartridges.UpdateCartridgeUiState(loader, new NCAgentMedTekUiState([.. ent.Comp.History]));
    }
}
