using System;
// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCHousingAndZoneDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_dungeon_run",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dungeon_slot_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    template_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    template_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    deployment_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    seed = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    result = table.Column<int>(type: "INTEGER", nullable: false),
                    error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_dungeon_run", x => x.run_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_record",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    building_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    purchase_price = table.Column<int>(type: "INTEGER", nullable: false),
                    acquired_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    access_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    rent_policy_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    access_policy_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    current_template_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    current_template_revision = table.Column<int>(type: "INTEGER", nullable: true),
                    active_deployment_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    interior_state = table.Column<string>(type: "TEXT", nullable: false),
                    next_rent_due_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_record", x => x.property_id);
                    table.ForeignKey(
                        name: "FK_nc_property_record_profile_profile_id",
                        column: x => x.owner_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "nc_zone_deployment_journal",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_zone_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    template_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    base_z = table.Column<int>(type: "INTEGER", nullable: false),
                    owner_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    dungeon_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    expected_zone_version = table.Column<int>(type: "INTEGER", nullable: false),
                    baseline_state = table.Column<string>(type: "TEXT", nullable: false),
                    error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_zone_deployment_journal", x => x.deployment_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_dungeon_extracted_item",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    extracted_by_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    persistent_state = table.Column<string>(type: "TEXT", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_dungeon_extracted_item", x => x.item_id);
                    table.ForeignKey(
                        name: "FK_nc_dungeon_extracted_item_nc_dungeon_run_run_temp_id",
                        column: x => x.run_id,
                        principalTable: "nc_dungeon_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_dungeon_extracted_item_profile_profile_id",
                        column: x => x.extracted_by_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "nc_dungeon_objective",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    objective_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_dungeon_objective", x => new { x.run_id, x.objective_id });
                    table.ForeignKey(
                        name: "FK_nc_dungeon_objective_nc_dungeon_run_run_temp_id1",
                        column: x => x.run_id,
                        principalTable: "nc_dungeon_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_dungeon_run_participant",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_dungeon_run_participant", x => new { x.run_id, x.profile_id });
                    table.ForeignKey(
                        name: "FK_nc_dungeon_run_participant_nc_dungeon_run_run_temp_id2",
                        column: x => x.run_id,
                        principalTable: "nc_dungeon_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_dungeon_run_participant_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_access_grant",
                columns: table => new
                {
                    grant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    property_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    permissions = table.Column<int>(type: "INTEGER", nullable: false),
                    access_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    issued_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    valid_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_access_grant", x => x.grant_id);
                    table.ForeignKey(
                        name: "FK_nc_property_access_grant_nc_property_record_property_temp_id",
                        column: x => x.property_id,
                        principalTable: "nc_property_record",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_property_access_grant_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_event",
                columns: table => new
                {
                    nc_property_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    property_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_type = table.Column<byte>(type: "INTEGER", nullable: false),
                    subject_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_event", x => x.nc_property_event_id);
                    table.ForeignKey(
                        name: "FK_nc_property_event_nc_property_record_property_temp_id1",
                        column: x => x.property_id,
                        principalTable: "nc_property_record",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_room",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    room_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_room", x => new { x.property_id, x.room_id });
                    table.ForeignKey(
                        name: "FK_nc_property_room_nc_property_record_property_id",
                        column: x => x.property_id,
                        principalTable: "nc_property_record",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_tenant",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    valid_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_tenant", x => new { x.property_id, x.profile_id });
                    table.ForeignKey(
                        name: "FK_nc_property_tenant_nc_property_record_property_id",
                        column: x => x.property_id,
                        principalTable: "nc_property_record",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_property_tenant_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_dungeon_extracted_item_extracted_by_profile_id",
                table: "nc_dungeon_extracted_item",
                column: "extracted_by_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_dungeon_extracted_item_run_id",
                table: "nc_dungeon_extracted_item",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_dungeon_run_deployment_id",
                table: "nc_dungeon_run",
                column: "deployment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_dungeon_run_dungeon_slot_id_state",
                table: "nc_dungeon_run",
                columns: new[] { "dungeon_slot_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_dungeon_run_participant_profile_id",
                table: "nc_dungeon_run_participant",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_access_grant_profile_id",
                table: "nc_property_access_grant",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_access_grant_property_id_profile_id_revoked_at",
                table: "nc_property_access_grant",
                columns: new[] { "property_id", "profile_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_event_property_id_created_at",
                table: "nc_property_event",
                columns: new[] { "property_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_record_owner_profile_id",
                table: "nc_property_record",
                column: "owner_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_record_status_building_id",
                table: "nc_property_record",
                columns: new[] { "status", "building_id" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_tenant_profile_id",
                table: "nc_property_tenant",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_zone_deployment_journal_target_zone_id_state",
                table: "nc_zone_deployment_journal",
                columns: new[] { "target_zone_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_dungeon_extracted_item");

            migrationBuilder.DropTable(
                name: "nc_dungeon_objective");

            migrationBuilder.DropTable(
                name: "nc_dungeon_run_participant");

            migrationBuilder.DropTable(
                name: "nc_property_access_grant");

            migrationBuilder.DropTable(
                name: "nc_property_event");

            migrationBuilder.DropTable(
                name: "nc_property_room");

            migrationBuilder.DropTable(
                name: "nc_property_tenant");

            migrationBuilder.DropTable(
                name: "nc_zone_deployment_journal");

            migrationBuilder.DropTable(
                name: "nc_dungeon_run");

            migrationBuilder.DropTable(
                name: "nc_property_record");
        }
    }
}
