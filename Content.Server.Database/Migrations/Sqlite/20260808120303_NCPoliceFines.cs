using System;
using Microsoft.EntityFrameworkCore.Migrations;
// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCPoliceFines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_police_fine",
                columns: table => new
                {
                    nc_police_fine_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    target_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    target_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    article = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    amount = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<byte>(type: "INTEGER", nullable: false),
                    issued_by_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    issued_by_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    issued_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    due_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    paid_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_police_fine", x => x.nc_police_fine_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_police_fine_event",
                columns: table => new
                {
                    nc_police_fine_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    fine_id = table.Column<long>(type: "INTEGER", nullable: false),
                    event_type = table.Column<byte>(type: "INTEGER", nullable: false),
                    previous_status = table.Column<byte>(type: "INTEGER", nullable: false),
                    new_status = table.Column<byte>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    actor_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    actor_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_police_fine_event", x => x.nc_police_fine_event_id);
                    table.ForeignKey(
                        name: "FK_nc_police_fine_event_nc_police_fine_fine_id",
                        column: x => x.fine_id,
                        principalTable: "nc_police_fine",
                        principalColumn: "nc_police_fine_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_police_fine_target_profile_id_status",
                table: "nc_police_fine",
                columns: new[] { "target_profile_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_police_fine_event_fine_id_created_at",
                table: "nc_police_fine_event",
                columns: new[] { "fine_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_police_fine_event");

            migrationBuilder.DropTable(
                name: "nc_police_fine");
        }
    }
}
