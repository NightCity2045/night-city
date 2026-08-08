// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite;

[DbContext(typeof(SqliteServerDbContext))]
[Migration("20260808160000_NCCharacterBank")]
public sealed class NCCharacterBank : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "nc_character_bank_account",
            columns: table => new
            {
                profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                account_number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                pin = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                balance = table.Column<int>(type: "INTEGER", nullable: false),
                updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_character_bank_account", value => value.profile_id);
                table.ForeignKey(
                    name: "FK_nc_character_bank_account_profile_profile_id",
                    column: value => value.profile_id,
                    principalTable: "profile",
                    principalColumn: "profile_id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_nc_character_bank_account_account_number",
            table: "nc_character_bank_account",
            column: "account_number",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "nc_character_bank_account");
    }
}
