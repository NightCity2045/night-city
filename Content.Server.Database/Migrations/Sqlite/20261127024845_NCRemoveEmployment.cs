using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCRemoveEmployment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_employment_event");

            migrationBuilder.DropTable(
                name: "nc_character_employment");

            migrationBuilder.DropColumn(
                name: "ncdepartment_preference",
                table: "profile");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ncdepartment_preference",
                table: "profile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "nc_character_employment",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    job_prototype_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    state = table.Column<byte>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "nc_employment_event",
                columns: table => new
                {
                    nc_employment_event_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    actor_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    actor_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    event_type = table.Column<byte>(type: "INTEGER", nullable: false),
                    new_job_prototype_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    new_state = table.Column<byte>(type: "INTEGER", nullable: false),
                    previous_job_prototype_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    previous_state = table.Column<byte>(type: "INTEGER", nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_employment_event", x => x.nc_employment_event_id);
                    table.ForeignKey(
                        name: "FK_nc_employment_event_nc_character_employment_employment_profile_id",
                        column: x => x.profile_id,
                        principalTable: "nc_character_employment",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_employment_job_prototype_id",
                table: "nc_character_employment",
                column: "job_prototype_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_employment_event_profile_id_created_at",
                table: "nc_employment_event",
                columns: new[] { "profile_id", "created_at" });
        }
    }
}
