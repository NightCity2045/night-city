using System.Linq; // NC
using Content.Server.Humanoid.Components;
using Content.Shared.Body;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;

namespace Content.Server.Humanoid.Systems;

public sealed partial class RandomHumanoidAppearanceSystem : EntitySystem
{
    [Dependency] private HumanoidProfileSystem _humanoidProfile = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private SharedVisualBodySystem _visualBody = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RandomHumanoidAppearanceComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, RandomHumanoidAppearanceComponent component, MapInitEvent args)
    {
        // If we have an initial profile/base layer set, do not randomize this humanoid.
        if (!TryComp<HumanoidProfileComponent>(uid, out var humanoid))
            return;

        var profile = HumanoidCharacterProfile.RandomWithSpecies(humanoid.Species);

        // NC-Edit-Start: Filter the new profile before applying it to the actual body organs.
        if (component.AllowedMarkingLayers is { } allowedLayers)
        {
            foreach (var markings in profile.Appearance.Markings.Values)
            {
                foreach (var layer in markings.Keys.ToArray())
                {
                    if (!allowedLayers.Contains(layer))
                        markings.Remove(layer);
                }
            }
        }
        // NC-Edit-End

        _visualBody.ApplyProfileTo(uid, profile);
        _humanoidProfile.ApplyProfileTo(uid, profile);

        if (component.RandomizeName)
            _metaData.SetEntityName(uid, profile.Name);
    }
}
