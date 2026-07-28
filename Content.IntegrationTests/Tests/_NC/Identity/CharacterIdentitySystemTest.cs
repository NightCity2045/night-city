using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Identity;
using Content.Shared._NC.Identity;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._NC.Identity;

[TestFixture]
public sealed class CharacterIdentitySystemTest : GameTest
{
    [Test]
    public async Task IdentityFollowsMindBetweenBodies()
    {
        var server = Pair.Server;
        var entityManager = server.ResolveDependency<IServerEntityManager>();

        await server.WaitAssertion(() =>
        {
            var identitySystem = entityManager.System<CharacterIdentitySystem>();
            var mindSystem = entityManager.System<SharedMindSystem>();

            var firstBody = entityManager.SpawnEntity(null, MapCoordinates.Nullspace);
            var secondBody = entityManager.SpawnEntity(null, MapCoordinates.Nullspace);
            entityManager.EnsureComponent<MindContainerComponent>(firstBody);
            entityManager.EnsureComponent<MindContainerComponent>(secondBody);

            var mind = mindSystem.CreateMind(null);
            mindSystem.TransferTo(mind, firstBody, mind: mind);

            var profileId = new ProfileId(42);
            var accountId = new NetUserId(Guid.NewGuid());

            Assert.That(identitySystem.TryBindIdentity(mind, profileId, accountId), Is.True);
            Assert.That(identitySystem.TryGetIdentity(firstBody, out var firstProfile, out var firstAccount), Is.True);
            Assert.That(firstProfile, Is.EqualTo(profileId));
            Assert.That(firstAccount, Is.EqualTo(accountId));

            mindSystem.TransferTo(mind, secondBody, mind: mind);

            Assert.That(identitySystem.TryGetIdentity(secondBody, out var secondProfile, out var secondAccount), Is.True);
            Assert.That(secondProfile, Is.EqualTo(profileId));
            Assert.That(secondAccount, Is.EqualTo(accountId));
        });
    }

    [Test]
    public async Task ConflictingIdentityBindIsRejected()
    {
        var server = Pair.Server;
        var entityManager = server.ResolveDependency<IServerEntityManager>();

        await server.WaitAssertion(() =>
        {
            var identitySystem = entityManager.System<CharacterIdentitySystem>();
            var mindSystem = entityManager.System<SharedMindSystem>();
            var mind = mindSystem.CreateMind(null);
            var accountId = new NetUserId(Guid.NewGuid());

            Assert.That(identitySystem.TryBindIdentity(mind, new ProfileId(10), accountId), Is.True);
            Assert.That(identitySystem.TryBindIdentity(mind, new ProfileId(11), accountId), Is.False);
            Assert.That(identitySystem.TryGetIdentity(mind, out var actualProfile, out _), Is.True);
            Assert.That(actualProfile, Is.EqualTo(new ProfileId(10)));
        });
    }
}
