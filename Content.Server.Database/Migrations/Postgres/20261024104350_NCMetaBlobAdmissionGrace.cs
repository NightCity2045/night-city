using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCMetaBlobAdmissionGrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_nc_meta_revision_reference_policy_version",
                table: "nc_meta_revision");

            migrationBuilder.AddColumn<long>(
                name: "admitted_until_utc_ticks",
                table: "nc_meta_blob",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_revision_reference_policy_version_aggregate_type_ag~",
                table: "nc_meta_revision",
                columns: new[] { "reference_policy_version", "aggregate_type", "aggregate_id", "aggregate_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_nc_meta_revision_reference_policy_version_aggregate_type_ag~",
                table: "nc_meta_revision");

            migrationBuilder.DropColumn(
                name: "admitted_until_utc_ticks",
                table: "nc_meta_blob");

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_revision_reference_policy_version",
                table: "nc_meta_revision",
                column: "reference_policy_version");
        }
    }
}
