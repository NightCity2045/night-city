// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20260812090000_NCMedicalCharacterSnapshots")]
public sealed class NCMedicalCharacterSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "nc_medical_character_snapshot",
            columns: table => new
            {
                profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                snapshot_version = table.Column<int>(type: "INTEGER", nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                payload = table.Column<string>(type: "TEXT", maxLength: 524288, nullable: false),
                captured_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_medical_character_snapshot", value => value.profile_id);
                table.CheckConstraint("CK_nc_medical_snapshot_payload_length", "length(payload) <= 524288");
                table.CheckConstraint("CK_nc_medical_snapshot_revision", "revision > 0");
                table.CheckConstraint("CK_nc_medical_snapshot_version", "snapshot_version > 0");
                table.ForeignKey(
                    name: "FK_nc_medical_character_snapshot_profile_profile_id",
                    column: value => value.profile_id,
                    principalTable: "profile",
                    principalColumn: "profile_id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "nc_medical_character_snapshot");
    }
}
