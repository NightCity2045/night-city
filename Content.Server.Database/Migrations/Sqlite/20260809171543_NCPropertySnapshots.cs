using System;
// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCPropertySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_property_snapshot",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    template_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    template_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    format_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    entity_count = table.Column<int>(type: "INTEGER", nullable: false),
                    resident_entity_count = table.Column<int>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_snapshot", x => x.property_id);
                    table.ForeignKey(
                        name: "FK_nc_property_snapshot_nc_property_record_ncproperty_record_property_id",
                        column: x => x.property_id,
                        principalTable: "nc_property_record",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_property_snapshot");
        }
    }
}
