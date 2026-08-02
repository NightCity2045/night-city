// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Roles.Jobs;

namespace Content.IntegrationTests.Tests._NC.Jobs;

[TestFixture]
public sealed class NCVanillaJobSpawnTest : GameTest
{
    private const string MapId = "NCVanillaJobSpawnTestMap";

    [TestPrototypes]
    private static readonly string PassengerMapPrototype = $"""
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
    public async Task OrdinaryRoundStartUsesVanillaOverflowJob()
    {
        var pair = Pair;
        pair.Server.CfgMan.SetCVar(CCVars.GameMap, MapId);

        Assert.Multiple(() =>
        {
            Assert.That(SharedGameTicker.FallbackOverflowJob.Id, Is.EqualTo("Passenger"));
            Assert.That(SharedGameTicker.FallbackOverflowJobName, Is.EqualTo("job-name-passenger"));
        });

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
        Assert.That(job, Is.EqualTo(SharedGameTicker.FallbackOverflowJob));

        await pair.Server.WaitPost(() => ticker.RestartRound());
    }
}
