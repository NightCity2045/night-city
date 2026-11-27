// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server._NC.City.Atlas;
using Content.Shared.Pinpointer;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Server._NC.City;

[TestFixture]
[TestOf(typeof(NCCityBuildingIndex))]
public sealed class NCCityBuildingIndexTest
{
    private const int Floor = SharedNavMapSystem.FloorMask;
    private const int Wall = SharedNavMapSystem.FloorMask | SharedNavMapSystem.WallMask;
    private const int Door = SharedNavMapSystem.FloorMask | SharedNavMapSystem.AirlockMask;

    /// <summary>
    /// Map legend: '.' street, '#' wall, 'D' door, 'i' indoor floor, ' ' void. Row 0 is the top of the picture.
    /// </summary>
    private static NavMapComponent Build(params string[] rows)
    {
        var navMap = new NavMapComponent();
        for (var row = 0; row < rows.Length; row++)
        {
            for (var x = 0; x < rows[row].Length; x++)
            {
                var data = rows[row][x] switch
                {
                    '.' or 'i' => Floor,
                    '#' => Wall,
                    'D' => Door,
                    _ => 0,
                };
                if (data == 0)
                    continue;

                var tile = new Vector2i(x, rows.Length - 1 - row);
                var origin = new Vector2i(
                    (int) MathF.Floor(tile.X / (float) SharedNavMapSystem.ChunkSize),
                    (int) MathF.Floor(tile.Y / (float) SharedNavMapSystem.ChunkSize));
                if (!navMap.Chunks.TryGetValue(origin, out var chunk))
                    navMap.Chunks[origin] = chunk = new NavMapChunk(origin);

                chunk.TileData[SharedNavMapSystem.GetTileIndex(tile - origin * SharedNavMapSystem.ChunkSize)] = data;
            }
        }

        return navMap;
    }

    private static Vector2i Tile(int x, int row, int height) => new(x, height - 1 - row);

    [Test]
    public void EnclosedAreasBehindWallsAndDoorsBecomeBuildings()
    {
        string[] map =
        [
            "..............",
            ".######.#####.",
            ".#iiii#.#iii#.",
            ".#iiiiD.#iii#.",
            ".#iiii#.##D##.",
            ".######.......",
            "..............",
        ];
        var index = new NCCityBuildingIndex();
        index.AddGrid(Build(map), Vector2i.Zero);
        var height = map.Length;

        Assert.Multiple(() =>
        {
            Assert.That(index.Buildings, Has.Count.EqualTo(2), "Two enclosed buildings are expected.");
            Assert.That(index.TryGetBuilding(Tile(0, 0, height), out _), Is.False, "Street is not a building.");
            Assert.That(index.TryGetBuilding(Tile(7, 3, height), out _), Is.False, "The lane between houses is street.");
            Assert.That(index.TryGetBuilding(Tile(1, 1, height), out _), Is.False, "Walls are not building floor.");
            Assert.That(index.TryGetBuilding(Tile(2, 2, height), out var west), Is.True);
            Assert.That(index.TryGetBuilding(Tile(6, 3, height), out var door), Is.True, "An entrance door belongs to its building.");
            Assert.That(door, Is.EqualTo(west));
            Assert.That(index.TryGetBuilding(Tile(10, 2, height), out var east), Is.True);
            Assert.That(east, Is.Not.EqualTo(west));
        });
    }

    [Test]
    public void InteriorDoorsJoinRoomsAndTinyPocketsAreIgnored()
    {
        string[] map =
        [
            "...........",
            ".#########.",
            ".#ii#iiii#.",
            ".#iiDiiii#.",
            ".#ii#iiii#.",
            ".####D####.",
            "...........",
            ".###.......",
            ".#i#.......",
            ".###.......",
            "...........",
        ];
        var index = new NCCityBuildingIndex();
        index.AddGrid(Build(map), Vector2i.Zero);
        var height = map.Length;

        Assert.Multiple(() =>
        {
            Assert.That(index.Buildings, Has.Count.EqualTo(1), "Rooms joined by an interior door are one building.");
            Assert.That(index.TryGetBuilding(Tile(2, 2, height), out var left), Is.True);
            Assert.That(index.TryGetBuilding(Tile(7, 3, height), out var right), Is.True);
            Assert.That(left, Is.EqualTo(right));
            Assert.That(index.TryGetBuilding(Tile(2, 8, height), out _), Is.False, "A one-tile pocket is not a building.");
        });
    }

    [Test]
    public void GridOffsetMovesBuildingsIntoWorldTiles()
    {
        string[] map =
        [
            "......",
            ".####.",
            ".#ii#.",
            ".#ii#.",
            ".####.",
            "......",
        ];
        var index = new NCCityBuildingIndex();
        index.AddGrid(Build(map), new Vector2i(100, -50));

        Assert.That(index.TryGetBuilding(new Vector2i(102, -48), out _), Is.True);
        Assert.That(index.TryGetBuilding(new Vector2i(2, 2), out _), Is.False);
    }

    [Test]
    public void DistrictOutlineContainsPoints()
    {
        var square = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        Assert.Multiple(() =>
        {
            Assert.That(NCCityAtlasSystem.Contains(square, new Vector2(5, 5)), Is.True);
            Assert.That(NCCityAtlasSystem.Contains(square, new Vector2(-1, 5)), Is.False);
            Assert.That(NCCityAtlasSystem.Contains(square, new Vector2(5, 11)), Is.False);
        });
    }
}
