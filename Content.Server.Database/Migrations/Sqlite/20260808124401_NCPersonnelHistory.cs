using System;
using Microsoft.EntityFrameworkCore.Migrations;
// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCPersonnelHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_employment_event",
                columns: table => new
                {
                    nc_employment_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    event_type = table.Column<byte>(type: "INTEGER", nullable: false),
                    previous_job_prototype_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    new_job_prototype_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    previous_state = table.Column<byte>(type: "INTEGER", nullable: true),
                    new_state = table.Column<byte>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    actor_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    actor_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_employment_event", x => x.nc_employment_event_id);
                    table.ForeignKey(
                        name: "FK_nc_employment_event_nc_character_employment_employment_profile_id",
                        column: x => x.profile_id,
                        principalTable: "nc_character_employment",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_employment_event_profile_id_created_at",
                table: "nc_employment_event",
                columns: new[] { "profile_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_employment_event");
        }
    }
}
