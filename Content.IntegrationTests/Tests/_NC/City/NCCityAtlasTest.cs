// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.City.Atlas;
using Content.Shared._NC.City.Atlas;
using Content.Shared._NC.Coordinates;
using Robust.Shared.ContentPack;

namespace Content.IntegrationTests.Tests._NC.City;

[TestFixture]
[TestOf(typeof(NCCityAtlasSystem))]
public sealed class NCCityAtlasTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    /// <summary>
    /// A district drawn in the city map is found by position, and the file the editor saves is a valid
    /// ncCityAtlas prototype that can be copied into Resources unchanged.
    /// </summary>
    [Test]
    public async Task SavedDistrictIsQueryableAndLoadsAsPrototype()
    {
        var atlas = Server.System<NCCityAtlasSystem>();
        var resources = Server.ResolveDependency<IResourceManager>();
        var prototypes = SProtoMan;
        var network = new NCZNetworkId(Guid.NewGuid());
        var prototypeId = $"NCCityAtlas{network.Value:N}";
        var outline = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };

        await Server.WaitAssertion(() =>
        {
            Assert.That(atlas.TrySaveDistrict(network, string.Empty, "  Test Ward  ", outline, out var message), Is.True, message);
            Assert.That(atlas.TryGetDistrict(new NCMapCoordinates(network, new Vector2(5, 5), 3), out var district), Is.True,
                "Districts apply to every floor.");
            Assert.That(district.Name, Is.EqualTo("Test Ward"));
            Assert.That(atlas.TryGetDistrict(new NCMapCoordinates(network, new Vector2(15, 5), 0), out _), Is.False);

            var path = NCCityAtlasSystem.SavePathFor(prototypeId);
            Assert.That(resources.UserData.Exists(path), Is.True);
            var yaml = resources.UserData.ReadAllText(path);
            try
            {
                prototypes.LoadString(yaml);
                prototypes.ResolveResults();
                var loaded = prototypes.Index<NCCityAtlasPrototype>(prototypeId);
                Assert.That(loaded.NetworkId, Is.EqualTo(network));
                Assert.That(loaded.Districts.Single().Name, Is.EqualTo("Test Ward"));
                Assert.That(loaded.Districts.Single().Vertices, Is.EqualTo(outline));
            }
            finally
            {
                prototypes.RemoveString(yaml);
            }

            Assert.That(atlas.TryDeleteDistrict(network, district.Id, out message), Is.True, message);
            Assert.That(atlas.TryGetDistrict(new NCMapCoordinates(network, new Vector2(5, 5), 0), out _), Is.False);
            resources.UserData.Delete(path);
        });
    }
}
