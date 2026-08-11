// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCAgentNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_agent_call_log",
                columns: table => new
                {
                    nc_agent_call_log_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    caller_profile_id = table.Column<int>(type: "integer", nullable: false),
                    target_profile_id = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    answered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<byte>(type: "smallint", nullable: false),
                    target_read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_call_log", x => x.nc_agent_call_log_id);
                    table.ForeignKey(
                        name: "FK_nc_agent_call_log_caller_profile_id",
                        column: x => x.caller_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_call_log_target_profile_id",
                        column: x => x.target_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_call_log_caller_profile_id",
                table: "nc_agent_call_log",
                column: "caller_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_call_log_ended_at",
                table: "nc_agent_call_log",
                column: "ended_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_call_log_target_profile_id_target_read_at",
                table: "nc_agent_call_log",
                columns: new[] { "target_profile_id", "target_read_at" });

            // Existing conversations predate unread notifications and must not alert every recipient after deployment.
            migrationBuilder.Sql("UPDATE nc_agent_message SET read_at = sent_at WHERE read_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_agent_call_log");
        }
    }
}
