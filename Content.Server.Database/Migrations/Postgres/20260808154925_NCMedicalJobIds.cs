using Microsoft.EntityFrameworkCore.Migrations;

// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCMedicalJobIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RenameJobId(migrationBuilder, "TraumaTeamChief", "NCMedicalChief");
            RenameJobId(migrationBuilder, "TraumaTeamDoctor", "NCMedicalDoctor");
            RenameJobId(migrationBuilder, "TraumaTeamCoroner", "NCMedicalCoroner");
            RenameJobId(migrationBuilder, "TraumaTeamPsych", "NCMedicalPsychologist");
            RenameJobId(migrationBuilder, "TraumaTeamIntern", "NCMedicalIntern");
            RenameJobId(migrationBuilder, "TraumaTeamTech", "NCMedicalTechnician");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RenameJobId(migrationBuilder, "NCMedicalChief", "TraumaTeamChief");
            RenameJobId(migrationBuilder, "NCMedicalDoctor", "TraumaTeamDoctor");
            RenameJobId(migrationBuilder, "NCMedicalCoroner", "TraumaTeamCoroner");
            RenameJobId(migrationBuilder, "NCMedicalPsychologist", "TraumaTeamPsych");
            RenameJobId(migrationBuilder, "NCMedicalIntern", "TraumaTeamIntern");
            RenameJobId(migrationBuilder, "NCMedicalTechnician", "TraumaTeamTech");
        }

        private static void RenameJobId(MigrationBuilder migrationBuilder, string oldId, string newId)
        {
            migrationBuilder.Sql($"UPDATE nc_character_employment SET job_prototype_id = '{newId}' WHERE job_prototype_id = '{oldId}'");
            migrationBuilder.Sql($"UPDATE nc_employment_event SET previous_job_prototype_id = '{newId}' WHERE previous_job_prototype_id = '{oldId}'");
            migrationBuilder.Sql($"UPDATE nc_employment_event SET new_job_prototype_id = '{newId}' WHERE new_job_prototype_id = '{oldId}'");
            migrationBuilder.Sql($"UPDATE job SET job_name = '{newId}' WHERE job_name = '{oldId}'");
            migrationBuilder.Sql($"UPDATE profile_role_loadout SET role_name = '{newId}' WHERE role_name = '{oldId}'");
            migrationBuilder.Sql($"UPDATE role_whitelists SET role_id = '{newId}' WHERE role_id = '{oldId}'");
            migrationBuilder.Sql($"UPDATE ban_role SET role_id = '{newId}' WHERE role_id = '{oldId}'");
        }
    }
}
