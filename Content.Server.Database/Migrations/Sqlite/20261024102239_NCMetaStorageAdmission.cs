using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCMetaStorageAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "reference_policy_version",
                table: "nc_meta_revision",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "nc_meta_blob_storage",
                columns: table => new
                {
                    nc_meta_blob_storage_id = table.Column<byte>(type: "INTEGER", nullable: false),
                    allocated_count = table.Column<long>(type: "INTEGER", nullable: false),
                    allocated_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    backfill_cursor = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    backfill_complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    count_tokens = table.Column<double>(type: "REAL", nullable: false),
                    byte_tokens = table.Column<double>(type: "REAL", nullable: false),
                    refilled_at_utc_ticks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob_storage", x => x.nc_meta_blob_storage_id);
                });

            migrationBuilder.InsertData(
                table: "nc_meta_blob_storage",
                columns: new[] { "nc_meta_blob_storage_id", "allocated_bytes", "allocated_count", "backfill_complete", "backfill_cursor", "byte_tokens", "count_tokens", "refilled_at_utc_ticks" },
                values: new object[] { (byte)1, 0L, 0L, false, "", 0.0, 0.0, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_revision_reference_policy_version",
                table: "nc_meta_revision",
                column: "reference_policy_version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_meta_blob_storage");

            migrationBuilder.DropIndex(
                name: "IX_nc_meta_revision_reference_policy_version",
                table: "nc_meta_revision");

            migrationBuilder.DropColumn(
                name: "reference_policy_version",
                table: "nc_meta_revision");
        }
    }
}
