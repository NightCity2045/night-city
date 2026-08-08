// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres;

public partial class NCPoliceCasesAndWarrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "nc_police_case",
            columns: table => new
            {
                nc_police_case_id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                status = table.Column<byte>(type: "smallint", nullable: false),
                created_by_profile_id = table.Column<int>(type: "integer", nullable: true),
                created_by_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_nc_police_case", x => x.nc_police_case_id));

        migrationBuilder.CreateTable(
            name: "nc_police_case_entry",
            columns: table => new
            {
                nc_police_case_entry_id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                case_id = table.Column<long>(type: "bigint", nullable: false),
                entry_type = table.Column<byte>(type: "smallint", nullable: false),
                text = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                previous_status = table.Column<byte>(type: "smallint", nullable: true),
                new_status = table.Column<byte>(type: "smallint", nullable: true),
                subject_profile_id = table.Column<int>(type: "integer", nullable: true),
                subject_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                subject_role = table.Column<byte>(type: "smallint", nullable: true),
                author_profile_id = table.Column<int>(type: "integer", nullable: true),
                author_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_police_case_entry", x => x.nc_police_case_entry_id);
                table.ForeignKey(
                    name: "FK_nc_police_case_entry_nc_police_case_case_id",
                    column: x => x.case_id,
                    principalTable: "nc_police_case",
                    principalColumn: "nc_police_case_id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "nc_police_case_subject",
            columns: table => new
            {
                case_id = table.Column<long>(type: "bigint", nullable: false),
                profile_id = table.Column<int>(type: "integer", nullable: false),
                character_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                role = table.Column<byte>(type: "smallint", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_police_case_subject", x => new { x.case_id, x.profile_id });
                table.ForeignKey(
                    name: "FK_nc_police_case_subject_nc_police_case_case_id",
                    column: x => x.case_id,
                    principalTable: "nc_police_case",
                    principalColumn: "nc_police_case_id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "nc_police_warrant",
            columns: table => new
            {
                nc_police_warrant_id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                case_id = table.Column<long>(type: "bigint", nullable: true),
                target_profile_id = table.Column<int>(type: "integer", nullable: false),
                target_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                type = table.Column<byte>(type: "smallint", nullable: false),
                status = table.Column<byte>(type: "smallint", nullable: false),
                reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                issued_by_profile_id = table.Column<int>(type: "integer", nullable: true),
                issued_by_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                resolved_by_profile_id = table.Column<int>(type: "integer", nullable: true),
                resolved_by_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                resolution_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_police_warrant", x => x.nc_police_warrant_id);
                table.ForeignKey(
                    name: "FK_nc_police_warrant_nc_police_case_case_id",
                    column: x => x.case_id,
                    principalTable: "nc_police_case",
                    principalColumn: "nc_police_case_id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_nc_police_case_entry_case_id_created_at",
            table: "nc_police_case_entry",
            columns: new[] { "case_id", "created_at" });
        migrationBuilder.CreateIndex(
            name: "IX_nc_police_case_subject_profile_id",
            table: "nc_police_case_subject",
            column: "profile_id");
        migrationBuilder.CreateIndex(
            name: "IX_nc_police_warrant_case_id",
            table: "nc_police_warrant",
            column: "case_id");
        migrationBuilder.CreateIndex(
            name: "IX_nc_police_warrant_target_profile_id_status",
            table: "nc_police_warrant",
            columns: new[] { "target_profile_id", "status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "nc_police_case_entry");
        migrationBuilder.DropTable(name: "nc_police_case_subject");
        migrationBuilder.DropTable(name: "nc_police_warrant");
        migrationBuilder.DropTable(name: "nc_police_case");
    }
}
