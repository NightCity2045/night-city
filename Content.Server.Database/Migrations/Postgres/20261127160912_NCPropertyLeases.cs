using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCPropertyLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_property",
                columns: table => new
                {
                    property_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_profile_id = table.Column<int>(type: "integer", nullable: true),
                    paid_until = table.Column<long>(type: "bigint", nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property", x => x.property_id);
                    table.ForeignKey(
                        name: "FK_nc_property_profile_tenant_profile_id",
                        column: x => x.tenant_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_tenant_profile_id",
                table: "nc_property",
                column: "tenant_profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_property");
        }
    }
}
