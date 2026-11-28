// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server._NC.Medical.Cyberware.AutoInstall;
using Content.Server._NC.Medical.Persistence;
using Content.Server.Database;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Presets;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware.Presets;

/// <summary>
/// Fits a role's cyberware preset once per character. The preset waits for the character's stored medical state to
/// load, so the restore cannot overwrite it, and the grant is recorded so the next round does not fit it again.
/// </summary>
public sealed partial class NCCyberwarePresetSystem : EntitySystem
{
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private NCCyberwareAutoInstallSystem _install = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCyberwarePresetComponent, NCMedicalRestoreCompletedEvent>(OnRestoreCompleted);
    }

    private void OnRestoreCompleted(Entity<NCCyberwarePresetComponent> ent, ref NCMedicalRestoreCompletedEvent args)
    {
        RemCompDeferred<NCCyberwarePresetComponent>(ent.Owner);
        if (args.Character.IsValid)
            _ = GrantOnce(ent.Owner, ent.Comp.Preset, args.Character.Value);
    }

    private async Task GrantOnce(EntityUid body, ProtoId<NCCyberwarePresetPrototype> preset, int profile)
    {
        bool granted;
        try
        {
            granted = await _db.HasNCCyberwarePresetGrantAsync(profile, preset.Id);
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to read cyberware preset {preset} of profile {profile}: {exception}");
            return;
        }

        if (granted)
            return;

        _tasks.RunOnMainThread(() =>
        {
            if (TerminatingOrDeleted(body) || !TryApplyPreset(body, preset))
                return;

            _ = RecordGrant(profile, preset);
        });
    }

    /// <summary>Fits every item of the preset the body does not already carry. True if the whole preset is in place.</summary>
    public bool TryApplyPreset(EntityUid body, ProtoId<NCCyberwarePresetPrototype> presetId)
    {
        if (!_prototypes.TryIndex(presetId, out var preset))
            return false;

        var complete = true;
        foreach (var item in preset.Items)
        {
            if (!_prototypes.Index(item).TryGetComponent<NCCyberwareComponent>(out var module, _factory))
            {
                complete = false;
                continue;
            }

            if (_install.HasInstalled(body, module.Profile))
                continue;

            if (!_install.TryAutoInstall(body, item, out var failure))
            {
                Log.Warning($"Cyberware preset {presetId} could not fit {item} into {ToPrettyString(body)}: {failure}");
                complete = false;
            }
        }

        return complete;
    }

    private async Task RecordGrant(int profile, ProtoId<NCCyberwarePresetPrototype> preset)
    {
        try
        {
            await _db.AddNCCyberwarePresetGrantAsync(profile, preset.Id);
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to record cyberware preset {preset} of profile {profile}: {exception}");
        }
    }
}
