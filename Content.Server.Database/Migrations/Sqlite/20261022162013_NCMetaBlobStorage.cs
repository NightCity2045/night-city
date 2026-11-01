using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCMetaBlobStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_meta_blob",
                columns: table => new
                {
                    digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    byte_count = table.Column<int>(type: "INTEGER", nullable: false),
                    bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob", x => x.digest);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_blob_created_at",
                table: "nc_meta_blob",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_meta_blob");
        }
    }
}
