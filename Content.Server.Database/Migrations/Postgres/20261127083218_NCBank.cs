using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_bank_account",
                columns: table => new
                {
                    nc_bank_account_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: true),
                    department_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    balance = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_bank_account", x => x.nc_bank_account_id);
                    table.ForeignKey(
                        name: "FK_nc_bank_account_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_bank_fee_rate",
                columns: table => new
                {
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    basis_points = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_bank_fee_rate", x => x.category);
                });

            migrationBuilder.CreateTable(
                name: "nc_bank_salary",
                columns: table => new
                {
                    department_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    job_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_bank_salary", x => new { x.department_id, x.job_id });
                });

            migrationBuilder.CreateTable(
                name: "nc_bank_transaction",
                columns: table => new
                {
                    nc_bank_transaction_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<byte>(type: "smallint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    fee = table.Column<long>(type: "bigint", nullable: false),
                    counterparty = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_bank_transaction", x => x.nc_bank_transaction_id);
                    table.ForeignKey(
                        name: "FK_nc_bank_transaction_nc_bank_account_ncbank_account_id",
                        column: x => x.account_id,
                        principalTable: "nc_bank_account",
                        principalColumn: "nc_bank_account_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_account_department_id",
                table: "nc_bank_account",
                column: "department_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_account_number",
                table: "nc_bank_account",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_account_profile_id",
                table: "nc_bank_account",
                column: "profile_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_transaction_account_id_nc_bank_transaction_id",
                table: "nc_bank_transaction",
                columns: new[] { "account_id", "nc_bank_transaction_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_bank_fee_rate");

            migrationBuilder.DropTable(
                name: "nc_bank_salary");

            migrationBuilder.DropTable(
                name: "nc_bank_transaction");

            migrationBuilder.DropTable(
                name: "nc_bank_account");
        }
    }
}
