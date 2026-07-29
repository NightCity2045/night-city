using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCProgressionDataDrivenBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_nc_character_progression_spent_skill_points",
                table: "nc_character_progression");

            migrationBuilder.AddCheckConstraint(
                name: "CK_nc_character_progression_spent_skill_points",
                table: "nc_character_progression",
                sql: "spent_skill_points >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_nc_character_progression_spent_skill_points",
                table: "nc_character_progression");

            migrationBuilder.AddCheckConstraint(
                name: "CK_nc_character_progression_spent_skill_points",
                table: "nc_character_progression",
                sql: "spent_skill_points >= 0 AND spent_skill_points <= level * 10");
        }
    }
}
