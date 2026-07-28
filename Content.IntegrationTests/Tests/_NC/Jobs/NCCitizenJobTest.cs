using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Pair;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Roles;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Roles.Jobs;

namespace Content.IntegrationTests.Tests._NC.Jobs;

[TestFixture]
public sealed class NCCitizenJobTest : GameTest
{
    private const string MapId = "NCCitizenJobTestMap";

    [TestPrototypes]
    private static readonly string CitizenMapPrototype = $"""
        - type: gameMap
          id: {MapId}
          mapName: {MapId}
          mapPath: /Maps/Test/empty.yml
          minPlayers: 0
          stations:
            Empty:
              stationProto: StandardNanotrasenStation
              components:
                - type: StationNameSetup
                  mapNameTemplate: "Night City"
                - type: StationJobs
                  availableJobs:
                    Passenger: [ -1, -1 ]
        """;

    public override PoolSettings PoolSettings => new()
    {
        DummyTicker = false,
        Connected = true,
        InLobby = true,
    };

    [Test]
    public async Task OrdinaryRoundStartAlwaysAssignsCitizen()
    {
        var pair = Pair;
        pair.Server.CfgMan.SetCVar(NCCVars.SingleCitizenJob, true);
        pair.Server.CfgMan.SetCVar(CCVars.GameMap, MapId);

        var ticker = pair.Server.System<GameTicker>();
        ticker.ToggleReadyAll(true);

        await pair.Server.WaitPost(() => ticker.StartRound());
        await pair.RunTicksSync(10);

        var userId = pair.Client.User!.Value;
        Assert.That(ticker.PlayerGameStatuses[userId], Is.EqualTo(PlayerGameStatus.JoinedGame));

        var entity = pair.Server.PlayerMan.SessionsDict[userId].AttachedEntity;
        Assert.That(entity, Is.Not.Null);

        var mindSystem = pair.Server.System<MindSystem>();
        var jobSystem = pair.Server.System<SharedJobSystem>();
        var mind = mindSystem.GetMind(entity!.Value);

        Assert.That(jobSystem.MindTryGetJobId(mind, out var job));
        Assert.That(job, Is.EqualTo(NCJobIds.Citizen));

        await pair.Server.WaitPost(() => ticker.RestartRound());
    }
}
