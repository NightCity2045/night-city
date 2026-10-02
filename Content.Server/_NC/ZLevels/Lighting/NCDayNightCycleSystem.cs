// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared.Light.Components;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.ZLevels.Lighting;

/// <summary>
/// Configures one synchronized outdoor light cycle for every eligible map in an NC Z-level network.
/// </summary>
public sealed class NCDayNightCycleSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        // NCZMapComponent and its depth are assigned before shared map overrides initialize.
        SubscribeLocalEvent<NCDayNightCycleComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<NCDayNightCycleComponent> map, ref MapInitEvent args)
    {
        if (!TryComp<NCZMapComponent>(map, out var zLevel))
            return;

        ConfigureMap(map, zLevel.Depth, map.Comp);
    }

    private void ConfigureMap(EntityUid map, int depth, NCDayNightCycleComponent settings)
    {
        // Underground floors deliberately keep the static ambient color authored on the map.
        if (depth < settings.MinimumDepth)
        {
            RemComp<SunShadowCycleComponent>(map);
            RemComp<SunShadowComponent>(map);
            RemComp<LightCycleComponent>(map);
            return;
        }

        if (!TryComp<MapLightComponent>(map, out var mapLight))
            return;

        if (settings.Duration <= TimeSpan.Zero)
        {
            Log.Error($"Cannot configure NC day/night cycle on {ToPrettyString(map)}: duration must be positive.");
            return;
        }

        var normalizedHour = Math.Clamp(settings.RoundStartHour, 0f, 24f);
        // Calculate in double precision so exact hours such as 08:00 do not drift by sub-milliseconds.
        var offsetTicks = (long) Math.Round(settings.Duration.Ticks * (double) normalizedHour / 24d);
        var offset = TimeSpan.FromTicks(offsetTicks);

        var cycle = EnsureComp<LightCycleComponent>(map);
        cycle.OriginalColor = mapLight.AmbientLightColor;
        cycle.Duration = settings.Duration;
        cycle.Offset = offset;
        cycle.Enabled = true;
        cycle.InitialOffset = false;
        cycle.MinLightLevel = settings.MinLightLevel;
        cycle.MaxLightLevel = settings.MaxLightLevel;
        cycle.ClipLight = settings.ClipLight;
        cycle.ClipLevel = settings.ClipLevel;
        cycle.MinLevel = settings.MinLevel;
        cycle.MaxLevel = settings.MaxLevel;
        Dirty(map, cycle);

        EnsureComp<SunShadowComponent>(map);
        var shadowCycle = EnsureComp<SunShadowCycleComponent>(map);
        shadowCycle.Duration = settings.Duration;
        shadowCycle.Offset = offset;
        Dirty(map, shadowCycle);
    }
}
