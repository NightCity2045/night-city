// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Persistence.RoundSnapshot;

/// <summary>Chooses one committed manifest at the load boundary, including later player-inventory restores.</summary>
public sealed partial class NCRoundRestoreContextSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private ITaskManager _tasks = default!;
    public NCRoundSnapshotManifestData? Manifest { get; private set; }
    public bool SelectionFailed { get; private set; }
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted,
            before: [typeof(NCCityMapDeltaSnapshotSystem)]);
    }
    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Manifest = null;
        SelectionFailed = false;
        try
        {
            var task = _database.GetLatestCommittedNCRoundSnapshotAsync();
            _tasks.BlockWaitOnTask(task);
            Manifest = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            SelectionFailed = true;
            Log.Error($"Could not select committed world snapshot: {exception}");
        }
    }
}
