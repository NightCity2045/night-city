// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Persistence;
using Content.Server.Database;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.CCVar;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Paper;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Preferences;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Two independent test-host processes exchange production medical YAML and loose-item world YAML.
/// Run with Tools/_NC/Cyberware/verify_restart.ps1; an ordinary suite run skips both phases.
/// </summary>
[TestFixture]
public sealed class NCCyberwareRestartTest : GameTest
{
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _medical = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private MapLoaderSystem _loader = default!;
    [SidedDependency(Side.Server)] private ISerializationManager _serialization = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [TestCase("save"), TestCase("restore"), RunOnSide(Side.Server)]
    public void UsedChromeSurvivesTwoOwnersAndAProcessRestart(string phase)
    {
        if (Environment.GetEnvironmentVariable("NC_CYBERWARE_RESTART_PHASE") != phase)
            Assert.Ignore("Use the two-process restart runner to supply this phase.");
        var directory = Environment.GetEnvironmentVariable("NC_CYBERWARE_RESTART_DIRECTORY");
        Assert.That(directory, Is.Not.Null.And.Not.Empty);
        Assert.That(Path.IsPathFullyQualified(directory!), Is.True);
        Directory.CreateDirectory(directory!);
        _maps.CreateMap(out var map);
        var coordinates = new MapCoordinates(Vector2.Zero, map);
        var seller = SEntMan.SpawnEntity("MobHuman", coordinates);
        var buyer = SEntMan.SpawnEntity("MobHuman", coordinates);
        if (phase == "save")
            Save(directory!, seller, buyer);
        else
            Restore(directory!, seller, buyer);
    }

    private void Save(string directory, EntityUid seller, EntityUid buyer)
    {
        var databaseFile = "characters-" + Guid.NewGuid().ToString("N") + ".db";
        File.WriteAllText(Path.Combine(directory, "database-name.txt"), databaseFile);
        var database = Database(Path.Combine(directory, databaseFile));
        var sellerId = CreateCharacter(database);
        var buyerId = CreateCharacter(database);
        Bind(seller, sellerId);
        Bind(buyer, buyerId);
        File.WriteAllLines(Path.Combine(directory, "characters.txt"), [sellerId.ToString(), buyerId.ToString()]);
        NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, seller, 512.5f);
        NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, buyer, 333.25f);
        var module = SEntMan.SpawnEntity("NCSandevistanImplant", SComp<TransformComponent>(seller).Coordinates);
        Install(seller, module);
        var beforeSale = WriteMedical(directory, "before-sale", seller);
        Store(database, sellerId, beforeSale, 0);
        Assert.That(_cyberware.TryRemove(new(NCActionResolutionId.New(), seller, module)).Accepted, Is.True);

        var bench = SEntMan.SpawnEntity("NCCyberwareWorkbench", SComp<TransformComponent>(seller).Coordinates);
        var cell = SEntMan.SpawnEntity("PowerCellHigh", SComp<TransformComponent>(seller).Coordinates);
        Assert.That(_slots.TryInsert(bench, "nc_bench_cell", cell, null), Is.True);
        Assert.That(_hands.TryPickupAnyHand(seller, module), Is.True);
        Assert.That(_cyberware.TryMountAssembly(bench, seller, module), Is.True);
        var card = SEntMan.SpawnEntity("NCCyberwareServiceLockCard", SComp<TransformComponent>(seller).Coordinates);
        Assert.That(_hands.TryPickupAnyHand(seller, card), Is.True);
        var hardware = SComp<NCCyberwareComponent>(module);
        Assert.That(_cyberware.TryFlashFirmware(bench, seller, card, module, hardware.Service.Revision), Is.True);
        Assert.That(_hands.TryDrop(seller, card), Is.True);
        var paper = SEntMan.SpawnEntity("Paper", SComp<TransformComponent>(seller).Coordinates);
        Assert.That(_hands.TryPickupAnyHand(seller, paper), Is.True);
        Assert.That(_cyberware.TryWriteServiceDocument(bench, seller, paper), Is.True);
        Assert.That(_hands.TryDrop(seller, paper), Is.True);
        Assert.That(_cyberware.TryEjectAssembly(bench, seller, module), Is.True);
        Assert.That(_hands.TryDrop(seller, module), Is.True);
        Install(buyer, module);
        hardware.Heat = 7;
        hardware.PermanentDamage = 0.03f;
        SComp<NCOrganIntegrityComponent>(module).CurrentIntegrity -= 9;
        var ability = SComp<NCCyberwareAbilityComponent>(module);
        ability.CooldownRemainingSeconds = 23;
        ability.CooldownObservedAt = SGameTiming.CurTime;
        Store(database, sellerId, WriteMedical(directory, "seller", seller), 1);
        Store(database, buyerId, WriteMedical(directory, "buyer", buyer), 0);

        // An independent used modular item and the real printed service sheet use world/possession serialization.
        var arm = SEntMan.SpawnEntity("NCOrganHumanCyberArmLeft", SComp<TransformComponent>(seller).Coordinates);
        SComp<NCCyberwareComponent>(arm).Heat = 19;
        SComp<NCCyberwareComponent>(arm).PermanentDamage = 0.04f;
        SComp<NCCyberwareComponent>(arm).EmpDisabledUntil = SGameTiming.CurTime + TimeSpan.FromSeconds(13);
        SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity -= 12;
        Assert.That(_cyberware.TryCaptureOrganState(arm, out var loose), Is.True);
        File.WriteAllText(Path.Combine(directory, "loose-state.yml"), _serialization.WriteValue(loose, alwaysWrite: true).ToString());
        using var writer = new StringWriter();
        Assert.That(_loader.TrySaveGeneric([arm, paper], writer, out _, SerializationOptions.Default with
        {
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore, ErrorOnOrphan = false,
        }), Is.True);
        File.WriteAllText(Path.Combine(directory, "possessions.yml"), writer.ToString());
        File.WriteAllText(Path.Combine(directory, "save-process.txt"), Environment.ProcessId.ToString());
    }

    private void Restore(string directory, EntityUid seller, EntityUid buyer)
    {
        Assert.That(Environment.ProcessId.ToString(), Is.Not.EqualTo(File.ReadAllText(Path.Combine(directory, "save-process.txt"))));
        var ids = File.ReadAllLines(Path.Combine(directory, "characters.txt"));
        var sellerId = new NCCharacterId(int.Parse(ids[0]));
        var buyerId = new NCCharacterId(int.Parse(ids[1]));
        Bind(seller, sellerId);
        Bind(buyer, buyerId);
        var database = Database(Path.Combine(directory, File.ReadAllText(Path.Combine(directory, "database-name.txt"))));
        var buyerRow = database.GetNCMedicalSnapshotAsync(buyerId).GetAwaiter().GetResult();
        var sellerRow = database.GetNCMedicalSnapshotAsync(sellerId).GetAwaiter().GetResult();
        Assert.That(buyerRow, Is.Not.Null);
        Assert.That(sellerRow, Is.Not.Null);
        Assert.That(buyerRow!.Revision, Is.EqualTo(1));
        Assert.That(sellerRow!.Revision, Is.EqualTo(2));
        var expected = Decode<NCMedicalStateSnapshot>(buyerRow.Payload);
        Assert.That(_medical.RestoreMedicalState(buyer, expected), Is.True);
        Assert.That(_medical.RestoreMedicalState(seller, Decode<NCMedicalStateSnapshot>(sellerRow.Payload)), Is.True);
        Assert.That(_serialization.WriteValue(_medical.SerializeMedicalState(buyer)!.Humanity, alwaysWrite: true).ToString(),
            Is.EqualTo(_serialization.WriteValue(expected.Humanity, alwaysWrite: true).ToString()));
        var saved = expected.CyberwareModules.Single(value => value.Profile.Id == "NCSandevistan");
        var module = _cyberware.CollectCyberware(buyer).Single(uid => SComp<NCCyberwareComponent>(uid).StableInstanceId == saved.StableInstanceId);
        Assert.That(_cyberware.TryCaptureOrganState(module, out var actual), Is.True);
        Assert.That(_serialization.WriteValue(actual.Service, alwaysWrite: true).ToString(),
            Is.EqualTo(_serialization.WriteValue(saved.Service, alwaysWrite: true).ToString()));
        Assert.Multiple(() =>
        {
            Assert.That(actual.CurrentIntegrity, Is.EqualTo(saved.CurrentIntegrity));
            Assert.That(actual.PermanentDamage, Is.EqualTo(0.03f));
            Assert.That(actual.Heat, Is.EqualTo(7));
            Assert.That(actual.AbilityCooldownSeconds, Is.EqualTo(23));
            Assert.That(actual.Calibrated, Is.EqualTo(saved.Calibrated));
            Assert.That(actual.Service!.Firmware.Id, Is.EqualTo("NCFirmwareServiceLock"));
            Assert.That(actual.Service.History.Count(entry => entry.Operation == NCCyberwareServiceOperation.Installed), Is.EqualTo(2));
            Assert.That(actual.Service.History.Where(entry => entry.Operation == NCCyberwareServiceOperation.Installed)
                .Select(entry => entry.Owner), Is.EqualTo(new[] { "1:" + sellerId, "1:" + buyerId }));
            Assert.That(SComp<NCCyberwareAbilityComponent>(module).Active, Is.False);
            Assert.That(_cyberware.GetSupplyCharge(module), Is.EqualTo(333.25f));
        });
        Assert.That(_medical.RestoreMedicalState(seller, Read<NCMedicalStateSnapshot>(directory, "before-sale.yml")), Is.False);
        var stale = database.TrySaveNCMedicalSnapshotAsync(Row(sellerId,
            File.ReadAllText(Path.Combine(directory, "before-sale.yml")), 1), 1).GetAwaiter().GetResult();
        Assert.That(stale.Status, Is.EqualTo(NCMedicalSnapshotWriteStatus.Conflict));
        Assert.That(database.GetNCMedicalSnapshotAsync(sellerId).GetAwaiter().GetResult()!.Payload, Is.EqualTo(sellerRow.Payload));
        Assert.That(_cyberware.TryGetOwningBody(module, out var owner) && owner == buyer, Is.True);
        using var reader = File.OpenText(Path.Combine(directory, "possessions.yml"));
        Assert.That(_loader.TryLoadGeneric(reader, "Gate 8 process restart possessions", out var result, MapLoadOptions.Default), Is.True);
        Assert.That(result.Orphans, Has.Count.EqualTo(2));
        var arm = result.Orphans.Single(uid => SEntMan.HasComponent<NCCyberwareComponent>(uid));
        var loose = Read<NCCyberwareStateSnapshot>(directory, "loose-state.yml");
        Assert.That(_cyberware.TryCaptureOrganState(arm, out var restoredLoose), Is.True);
        Assert.That(SComp<NCCyberwareWorldSnapshotComponent>(arm).Restored, Is.True);
        Assert.That(restoredLoose.EmpDisabledUntil - SGameTiming.CurTime, Is.EqualTo(TimeSpan.FromSeconds(13)));
        // Absolute deadlines rebase to this process; every physical field must otherwise match exactly.
        loose.EmpDisabledUntil = restoredLoose.EmpDisabledUntil;
        Assert.That(_serialization.WriteValue(restoredLoose, alwaysWrite: true).ToString(),
            Is.EqualTo(_serialization.WriteValue(loose, alwaysWrite: true).ToString()));
        var paper = result.Orphans.Single(uid => SEntMan.HasComponent<PaperComponent>(uid));
        Assert.That(SComp<PaperComponent>(paper).Content, Does.Contain(saved.StableInstanceId.ToString("D")));
        Assert.That(SComp<PaperComponent>(paper).EditingDisabled, Is.False);
        File.WriteAllText(Path.Combine(directory, "restore-process.txt"), Environment.ProcessId.ToString());
    }

    private void Install(EntityUid body, EntityUid module)
    {
        Assert.That(_anatomy.TryGetOrgan(body, "Head", out var head), Is.True);
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), body, head, module, "NCSlotMotorBurst", true, true)).Accepted, Is.True);
    }

    private string WriteMedical(string directory, string name, EntityUid body)
    {
        var state = _medical.SerializeMedicalState(body);
        Assert.That(state, Is.Not.Null);
        var payload = _serialization.WriteValue(state, alwaysWrite: true, notNullableOverride: true).ToString();
        File.WriteAllText(Path.Combine(directory, name + ".yml"), payload);
        return payload;
    }

    private T Read<T>(string directory, string name)
    {
        using var reader = File.OpenText(Path.Combine(directory, name));
        return _serialization.Read<T>(DataNodeParser.ParseYamlStream(reader).Single().Root, notNullableOverride: true);
    }

    private T Decode<T>(string payload)
    {
        using var reader = new StringReader(payload);
        return _serialization.Read<T>(DataNodeParser.ParseYamlStream(reader).Single().Root, notNullableOverride: true);
    }

    private ServerDbSqlite Database(string path)
    {
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite("Data Source=" + path);
        var configuration = Server.ResolveDependency<IConfigurationManager>();
        var concurrency = configuration.GetCVar(CCVars.DatabaseSqliteConcurrency);
        try
        {
            // This phase runs on the server thread; the real on-disk DB must use its synchronous fixture mode.
            configuration.SetCVar(CCVars.DatabaseSqliteConcurrency, 1);
            return new ServerDbSqlite(() => options.Options, false, configuration,
                true, Server.ResolveDependency<ILogManager>().GetSawmill("db.ops"), _serialization);
        }
        finally
        {
            configuration.SetCVar(CCVars.DatabaseSqliteConcurrency, concurrency);
        }
    }

    private static NCCharacterId CreateCharacter(ServerDbSqlite database)
    {
        var preference = database.InitPrefsAsync(new NetUserId(Guid.NewGuid()), new HumanoidCharacterProfile()).GetAwaiter().GetResult();
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private void Bind(EntityUid body, NCCharacterId character)
    {
        var owner = SEntMan.EnsureComponent<NCMedicalPersistenceOwnerComponent>(body);
        owner.Kind = NCMedicalStableOwnerKind.PlayerCharacter;
        owner.Key = character.ToString();
    }

    private static NCMedicalStoredSnapshotData Row(NCCharacterId character, string payload, long revision)
        => new(character, NCMedicalSnapshotVersions.Current, revision, payload, DateTime.UtcNow, DateTime.UtcNow);

    private static void Store(ServerDbSqlite database, NCCharacterId character, string payload, long revision)
        => Assert.That(database.TrySaveNCMedicalSnapshotAsync(Row(character, payload, revision), revision)
            .GetAwaiter().GetResult().Status, Is.EqualTo(NCMedicalSnapshotWriteStatus.Saved));
}
