using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCPropertyResale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "resale_buyer",
                table: "nc_property",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "resale_price",
                table: "nc_property",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resale_seller",
                table: "nc_property",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resale_buyer",
                table: "nc_property");

            migrationBuilder.DropColumn(
                name: "resale_price",
                table: "nc_property");

            migrationBuilder.DropColumn(
                name: "resale_seller",
                table: "nc_property");
        }
    }
}
