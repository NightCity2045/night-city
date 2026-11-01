// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Server._NC.Meta.Demo;
using NightCity.Diagnostics.Contracts;

namespace Content.Server._NC.Diagnostics;

public sealed partial class NCDiagnosticsSystem
{
    [Dependency] private NCMetaDemoSceneSystem _metaDemos = default!;

    private JsonObject CreateMetaDemo(JsonObject args)
    {
        RequireFixtureAdmin();
        if (_fixtures.Count >= 8) throw new DiagnosticException("fixture_limit", "The bounded fixture inventory is full.");
        var prototype = args.RequiredString("scene", 128);
        if (!_metaDemos.TryCreate(prototype, out var scene))
            throw new DiagnosticException("fixture_rejected", "The physical META scene could not be created.");
        var key = scene.Id.ToString("N");
        var fixture = new Fixture(scene.Map) { Grid = scene.Grid };
        foreach (var entity in scene.Objects.Values.Concat(scene.Cables).Append(scene.Operator).Append(scene.PowerSupply))
            fixture.Entities.Add(entity);
        _fixtures.Add(key, fixture);
        var objects = new JsonObject();
        foreach (var (name, uid) in scene.Objects) objects[name] = GetNetEntity(uid).ToString();
        return new JsonObject { ["fixtureId"] = key, ["scene"] = scene.Scene, ["map"] = GetNetEntity(scene.Map).ToString(),
            ["grid"] = GetNetEntity(scene.Grid).ToString(), ["operator"] = GetNetEntity(scene.Operator).ToString(),
            ["powerSupply"] = GetNetEntity(scene.PowerSupply).ToString(), ["objects"] = objects,
            ["ownershipGranted"] = false, ["automationEnabled"] = false, ["isolated"] = true };
    }

    private JsonObject DestroyMetaDemo(JsonObject args)
    {
        RequireFixtureAdmin();
        var id = args.RequiredString("fixtureId", 64);
        if (!Guid.TryParseExact(id, "N", out var guid) || !_metaDemos.TryDestroy(guid))
            throw new DiagnosticException("fixture_not_found", "The META scene does not exist.");
        _fixtures.Remove(id);
        return new JsonObject { ["fixtureId"] = id, ["deletionQueued"] = true };
    }

    private JsonObject ReadMetaDemos()
    {
        RequireFixtureAdmin();
        var scenes = new JsonArray();
        foreach (var scene in _metaDemos.GetScenes()) scenes.Add(new JsonObject
            { ["fixtureId"] = scene.Id.ToString("N"), ["scene"] = scene.Scene, ["map"] = GetNetEntity(scene.Map).ToString() });
        return new JsonObject { ["scenes"] = scenes };
    }

    private JsonNode ReadMetaDemoStatus(JsonObject args)
    {
        RequireFixtureAdmin();
        if (!Guid.TryParseExact(args.RequiredString("fixtureId", 64), "N", out var id) || _metaDemos.GetStatus(id) is not { } status)
            throw new DiagnosticException("fixture_not_found", "The META scene does not exist.");
        return JsonSerializer.SerializeToNode(status, DiagnosticProtocol.JsonOptions)!;
    }
}
#endif
