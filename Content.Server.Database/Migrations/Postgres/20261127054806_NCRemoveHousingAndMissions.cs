using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCRemoveHousingAndMissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                name: "nc_property_snapshot");

            migrationBuilder.DropTable(
                name: "nc_property_tenant");

            migrationBuilder.DropTable(
                name: "nc_zone_deployment_journal");

            migrationBuilder.DropTable(
                name: "nc_dungeon_run");

            migrationBuilder.DropTable(
                name: "nc_property_record");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_dungeon_run",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    definition_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dungeon_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    result = table.Column<int>(type: "integer", nullable: false),
                    seed = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    template_revision = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_dungeon_run", x => x.run_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_record",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_policy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    access_revision = table.Column<int>(type: "integer", nullable: false),
                    acquired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    active_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    current_template_revision = table.Column<int>(type: "integer", nullable: true),
                    interior_state = table.Column<string>(type: "text", nullable: false),
                    next_rent_due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    owner_profile_id = table.Column<int>(type: "integer", nullable: true),
                    purchase_price = table.Column<int>(type: "integer", nullable: false),
                    rent_policy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
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
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_z = table.Column<int>(type: "integer", nullable: false),
                    baseline_state = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    dungeon_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    expected_zone_version = table.Column<int>(type: "integer", nullable: false),
                    owner_profile_id = table.Column<int>(type: "integer", nullable: true),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    target_zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    template_revision = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_zone_deployment_journal", x => x.deployment_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_dungeon_extracted_item",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    extracted_by_profile_id = table.Column<int>(type: "integer", nullable: true),
                    persistent_state = table.Column<string>(type: "text", nullable: false)
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
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    objective_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
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
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false)
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
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_revision = table.Column<int>(type: "integer", nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    permissions = table.Column<int>(type: "integer", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    nc_property_event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<byte>(type: "smallint", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject_profile_id = table.Column<int>(type: "integer", nullable: true)
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
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                name: "nc_property_snapshot",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_count = table.Column<int>(type: "integer", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    resident_entity_count = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    template_revision = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_snapshot", x => x.property_id);
                    table.ForeignKey(
                        name: "FK_nc_property_snapshot_nc_property_record_ncproperty_record_p~",
                        column: x => x.property_id,
                        principalTable: "nc_property_record",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_tenant",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
    }
}
