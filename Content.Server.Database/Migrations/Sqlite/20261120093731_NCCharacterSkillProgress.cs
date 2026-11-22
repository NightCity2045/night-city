using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCCharacterSkillProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_character_skill_progress",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    active_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    learned_skills = table.Column<string>(type: "TEXT", nullable: false),
                    survived_rounds = table.Column<int>(type: "INTEGER", nullable: false),
                    last_survived_round = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_skill_progress", x => x.profile_id);
                    table.ForeignKey(
                        name: "FK_nc_character_skill_progress_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_character_skill_progress");
        }
    }
}
