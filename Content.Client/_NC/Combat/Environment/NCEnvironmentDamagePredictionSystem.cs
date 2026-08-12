// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Damage;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Damage.Systems;

namespace Content.Client._NC.Combat.Environment;

/// <summary>
/// Prevents predicted legacy HP mutation for authoritative NC bodies. The server alone creates medical trauma.
/// </summary>
public sealed partial class NCEnvironmentDamagePredictionSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, BeforeDamageChangedEvent>(
            OnBeforeDamageChanged,
            after: [typeof(GodmodeSystem)]);
    }

    private void OnBeforeDamageChanged(
        Entity<NCMedicalBodyComponent> target,
        ref BeforeDamageChangedEvent args)
    {
        if (args.Cancelled)
            return;

        // Client prediction may render feedback, but it cannot own wounds or global compatibility HP.
        args.Cancelled = true;
    }
}
