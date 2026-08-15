// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite;

/// <inheritdoc />
public partial class NCPersistenceFoundation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "nc_active_world_clock",
            columns: table => new
            {
                id = table.Column<byte>(type: "INTEGER", nullable: false),
                session_epoch = table.Column<Guid>(type: "TEXT", nullable: false),
                active_world_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                round_active = table.Column<bool>(type: "INTEGER", nullable: false),
                version = table.Column<int>(type: "INTEGER", nullable: false),
                updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_active_world_clock", value => value.id);
                table.CheckConstraint("CK_nc_active_world_clock_singleton", "id = 1");
                table.CheckConstraint("CK_nc_active_world_clock_ticks", "active_world_ticks >= 0");
            });

        migrationBuilder.CreateTable(
            name: "nc_aggregate_snapshot",
            columns: table => new
            {
                aggregate_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                aggregate_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                aggregate_version = table.Column<long>(type: "INTEGER", nullable: false),
                format_version = table.Column<int>(type: "INTEGER", nullable: false),
                session_epoch = table.Column<Guid>(type: "TEXT", nullable: false),
                active_world_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                payload = table.Column<string>(type: "TEXT", nullable: false),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_aggregate_snapshot", value => new { value.aggregate_type, value.aggregate_id });
            });

        migrationBuilder.CreateTable(
            name: "nc_command_receipt",
            columns: table => new
            {
                command_id = table.Column<Guid>(type: "TEXT", nullable: false),
                command_type = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                scope_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                scope_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                state = table.Column<byte>(type: "INTEGER", nullable: false),
                session_epoch = table.Column<Guid>(type: "TEXT", nullable: false),
                started_at_world_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                completed_at_world_ticks = table.Column<long>(type: "INTEGER", nullable: true),
                result_payload = table.Column<string>(type: "TEXT", nullable: true),
                error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_command_receipt", value => value.command_id);
            });

        migrationBuilder.CreateTable(
            name: "nc_event_journal",
            columns: table => new
            {
                event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                event_type = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                aggregate_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                aggregate_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                expected_version = table.Column<long>(type: "INTEGER", nullable: false),
                aggregate_version = table.Column<long>(type: "INTEGER", nullable: false),
                correlation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                causation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                session_epoch = table.Column<Guid>(type: "TEXT", nullable: false),
                active_world_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                payload = table.Column<string>(type: "TEXT", nullable: false),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                applied_at = table.Column<DateTime>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_event_journal", value => value.event_id);
            });

        migrationBuilder.CreateTable(
            name: "nc_outbox_message",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                message_type = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                payload = table.Column<string>(type: "TEXT", nullable: false),
                attempts = table.Column<int>(type: "INTEGER", nullable: false),
                available_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                processed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                last_error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_nc_outbox_message", value => value.message_id);
                table.ForeignKey(
                    name: "FK_nc_outbox_message_nc_event_journal_ncevent_journal_entry_event_id",
                    column: value => value.event_id,
                    principalTable: "nc_event_journal",
                    principalColumn: "event_id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_nc_command_receipt_scope_type_scope_id_created_at",
            table: "nc_command_receipt",
            columns: new[] { "scope_type", "scope_id", "created_at" });
        migrationBuilder.CreateIndex(
            name: "IX_nc_command_receipt_state_updated_at",
            table: "nc_command_receipt",
            columns: new[] { "state", "updated_at" });
        migrationBuilder.CreateIndex(
            name: "IX_nc_event_journal_aggregate_type_aggregate_id_aggregate_version",
            table: "nc_event_journal",
            columns: new[] { "aggregate_type", "aggregate_id", "aggregate_version" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_nc_event_journal_correlation_id_created_at",
            table: "nc_event_journal",
            columns: new[] { "correlation_id", "created_at" });
        migrationBuilder.CreateIndex(
            name: "IX_nc_outbox_message_event_id",
            table: "nc_outbox_message",
            column: "event_id");
        migrationBuilder.CreateIndex(
            name: "IX_nc_outbox_message_processed_at_available_at",
            table: "nc_outbox_message",
            columns: new[] { "processed_at", "available_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "nc_active_world_clock");
        migrationBuilder.DropTable(name: "nc_aggregate_snapshot");
        migrationBuilder.DropTable(name: "nc_command_receipt");
        migrationBuilder.DropTable(name: "nc_outbox_message");
        migrationBuilder.DropTable(name: "nc_event_journal");
    }
}
