using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCRemoveBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_character_bank_account");

            migrationBuilder.DropTable(
                name: "nc_organization_bank_transaction");

            migrationBuilder.DropTable(
                name: "nc_organization_bank_account");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_character_bank_account",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    account_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    balance = table.Column<int>(type: "integer", nullable: false),
                    pin = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_bank_account", x => x.profile_id);
                    table.ForeignKey(
                        name: "FK_nc_character_bank_account_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_organization_bank_account",
                columns: table => new
                {
                    organization_prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    balance = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_organization_bank_account", x => x.organization_prototype_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_organization_bank_transaction",
                columns: table => new
                {
                    nc_organization_bank_transaction_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor_profile_id = table.Column<int>(type: "integer", nullable: true),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    balance_after = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    type = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_organization_bank_transaction", x => x.nc_organization_bank_transaction_id);
                    table.ForeignKey(
                        name: "FK_nc_organization_bank_transaction_nc_organization_bank_accou~",
                        column: x => x.organization_prototype_id,
                        principalTable: "nc_organization_bank_account",
                        principalColumn: "organization_prototype_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_bank_account_account_number",
                table: "nc_character_bank_account",
                column: "account_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_organization_bank_transaction_organization_prototype_id_~",
                table: "nc_organization_bank_transaction",
                columns: new[] { "organization_prototype_id", "created_at" });
        }
    }
}
