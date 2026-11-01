using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
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
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    byte_count = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
