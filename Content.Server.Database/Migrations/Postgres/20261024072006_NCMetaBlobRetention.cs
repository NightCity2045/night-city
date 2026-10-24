using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCMetaBlobRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_meta_blob_maintenance",
                columns: table => new
                {
                    nc_meta_blob_maintenance_id = table.Column<byte>(type: "smallint", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    aggregate_type_cursor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id_cursor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    aggregates_complete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob_maintenance", x => x.nc_meta_blob_maintenance_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_checkpoint_blob_backfill",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    next_chunk = table.Column<int>(type: "integer", nullable: false),
                    digest_cursor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    complete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_checkpoint_blob_backfill", x => x.snapshot_id);
                    table.ForeignKey(
                        name: "FK_nc_meta_checkpoint_blob_backfill_nc_round_snapshot_ncround_~",
                        column: x => x.snapshot_id,
                        principalTable: "nc_round_snapshot",
                        principalColumn: "snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_checkpoint_blob_reference",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_checkpoint_blob_reference", x => new { x.snapshot_id, x.digest });
                    table.ForeignKey(
                        name: "FK_nc_meta_checkpoint_blob_reference_nc_meta_blob_digest",
                        column: x => x.digest,
                        principalTable: "nc_meta_blob",
                        principalColumn: "digest",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_meta_checkpoint_blob_reference_nc_round_snapshot_snapsho~",
                        column: x => x.snapshot_id,
                        principalTable: "nc_round_snapshot",
                        principalColumn: "snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_revision",
                columns: table => new
                {
                    aggregate_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    session_epoch = table.Column<Guid>(type: "uuid", nullable: false),
                    active_world_ticks = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_revision", x => new { x.aggregate_type, x.aggregate_id, x.aggregate_version });
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_blob_reference",
                columns: table => new
                {
                    aggregate_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob_reference", x => new { x.aggregate_type, x.aggregate_id, x.aggregate_version, x.digest });
                    table.ForeignKey(
                        name: "FK_nc_meta_blob_reference_nc_meta_blob_digest",
                        column: x => x.digest,
                        principalTable: "nc_meta_blob",
                        principalColumn: "digest",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_meta_blob_reference_nc_meta_revision_aggregate_type_aggr~",
                        columns: x => new { x.aggregate_type, x.aggregate_id, x.aggregate_version },
                        principalTable: "nc_meta_revision",
                        principalColumns: new[] { "aggregate_type", "aggregate_id", "aggregate_version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_blob_reference_digest",
                table: "nc_meta_blob_reference",
                column: "digest");

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_checkpoint_blob_reference_digest",
                table: "nc_meta_checkpoint_blob_reference",
                column: "digest");

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_revision_created_at",
                table: "nc_meta_revision",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_meta_blob_maintenance");

            migrationBuilder.DropTable(
                name: "nc_meta_blob_reference");

            migrationBuilder.DropTable(
                name: "nc_meta_checkpoint_blob_backfill");

            migrationBuilder.DropTable(
                name: "nc_meta_checkpoint_blob_reference");

            migrationBuilder.DropTable(
                name: "nc_meta_revision");
        }
    }
}
