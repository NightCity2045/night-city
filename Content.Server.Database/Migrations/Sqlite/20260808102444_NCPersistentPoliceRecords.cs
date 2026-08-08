using System;
using Microsoft.EntityFrameworkCore.Migrations;

// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCPersistentPoliceRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_police_record",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<byte>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    updated_by_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    updated_by_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_police_record", x => x.profile_id);
                    table.ForeignKey(
                        name: "FK_nc_police_record_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_police_record_event",
                columns: table => new
                {
                    nc_police_record_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    event_type = table.Column<byte>(type: "INTEGER", nullable: false),
                    previous_status = table.Column<byte>(type: "INTEGER", nullable: false),
                    new_status = table.Column<byte>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    actor_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    actor_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_police_record_event", x => x.nc_police_record_event_id);
                    table.ForeignKey(
                        name: "FK_nc_police_record_event_nc_police_record_profile_id",
                        column: x => x.profile_id,
                        principalTable: "nc_police_record",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_police_record_event_profile_id_created_at",
                table: "nc_police_record_event",
                columns: new[] { "profile_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_police_record_event");

            migrationBuilder.DropTable(
                name: "nc_police_record");
        }
    }
}
