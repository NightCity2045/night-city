// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Server.Database;
using NightCity.Diagnostics.Seeds;

try
{
    SQLitePCL.Batteries_V2.Init();
    object report;
    if (args.Length == 4) // Preserve the original explicit package producer interface.
        report = NCMetaSqliteSeedPackage.CreatePackage(args[0], args[1], args[2], NCMetaSqliteSeedPackage.ReadSqliteMigrationIds(args[3]));
    else if (args is ["stage-helper", var workspace, var seedId]) report = SqliteSeedCommands.StageHelper(workspace, seedId);
    else if (args is ["validate", var workspace2, var seedId2, var seedHash2, var manifestHash2, var database, var databaseHash2])
        report = SqliteSeedCommands.Validate(new(workspace2, seedId2, seedHash2, manifestHash2), database, databaseHash2);
    else if (args is ["materialize", var workspace3, var seedId3, var seedHash3, var manifestHash3, var environmentId, var databaseHash3])
        report = SqliteSeedCommands.Materialize(new(workspace3, seedId3, seedHash3, manifestHash3), environmentId, databaseHash3);
    else throw new ArgumentException("Expected package arguments, stage-helper, validate, or materialize.");
    var json = JsonSerializer.Serialize(report, SqliteSeedArtifact.Json);
    if (System.Text.Encoding.UTF8.GetByteCount(json) > SqliteSeedArtifact.MaximumReportBytes) throw new InvalidDataException("Helper report size limit.");
    Console.WriteLine(json);
    return 0;
}
catch (Exception failure) when (failure is ArgumentException or InvalidDataException or InvalidOperationException or IOException or
    UnauthorizedAccessException or System.Data.Common.DbException or JsonException or BadImageFormatException)
{
    // No stack/source/full inventory in the process protocol.
    Console.Error.WriteLine("SQLite seed helper rejected the request (" + failure.GetType().Name + ").");
    return 1;
}
