using System;
// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCCharacterEmployment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ncdepartment_preference",
                table: "profile",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "nc_character_employment",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    job_prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<byte>(type: "smallint", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_employment", x => x.profile_id);
                    table.ForeignKey(
                        name: "FK_nc_character_employment_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_employment_job_prototype_id",
                table: "nc_character_employment",
                column: "job_prototype_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_character_employment");

            migrationBuilder.DropColumn(
                name: "ncdepartment_preference",
                table: "profile");
        }
    }
}
