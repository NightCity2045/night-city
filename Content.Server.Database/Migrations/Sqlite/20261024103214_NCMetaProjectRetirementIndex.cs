using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCMetaProjectRetirementIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_nc_aggregate_snapshot_aggregate_type_aggregate_version_created_at_aggregate_id",
                table: "nc_aggregate_snapshot",
                columns: new[] { "aggregate_type", "aggregate_version", "created_at", "aggregate_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_nc_aggregate_snapshot_aggregate_type_aggregate_version_created_at_aggregate_id",
                table: "nc_aggregate_snapshot");
        }
    }
}
