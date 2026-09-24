using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCRoundSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_round_snapshot",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_epoch = table.Column<Guid>(type: "TEXT", nullable: false),
                    active_world_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    map_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    map_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    map_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<byte>(type: "INTEGER", nullable: false),
                    section_count = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    checksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    captured_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    committed_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_round_snapshot", x => x.snapshot_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_round_snapshot_section",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    section_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    chunk_index = table.Column<int>(type: "INTEGER", nullable: false),
                    format_version = table.Column<int>(type: "INTEGER", nullable: false),
                    entity_count = table.Column<int>(type: "INTEGER", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    checksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_round_snapshot_section", x => new { x.snapshot_id, x.section_type, x.owner_type, x.owner_id, x.chunk_index });
                    table.ForeignKey(
                        name: "FK_nc_round_snapshot_section_nc_round_snapshot_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "nc_round_snapshot",
                        principalColumn: "snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_round_snapshot_session_epoch",
                table: "nc_round_snapshot",
                column: "session_epoch",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_round_snapshot_status_committed_at",
                table: "nc_round_snapshot",
                columns: new[] { "status", "committed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_round_snapshot_section_section_type_owner_type_owner_id",
                table: "nc_round_snapshot_section",
                columns: new[] { "section_type", "owner_type", "owner_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_round_snapshot_section");

            migrationBuilder.DropTable(
                name: "nc_round_snapshot");
        }
    }
}
