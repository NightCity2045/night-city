using System;
using Microsoft.EntityFrameworkCore.Migrations;

// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCOrganizationBudgetTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_organization_bank_transaction",
                columns: table => new
                {
                    nc_organization_bank_transaction_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    organization_prototype_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    type = table.Column<byte>(type: "INTEGER", nullable: false),
                    amount = table.Column<int>(type: "INTEGER", nullable: false),
                    balance_after = table.Column<int>(type: "INTEGER", nullable: false),
                    actor_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    actor_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_organization_bank_transaction", x => x.nc_organization_bank_transaction_id);
                    table.ForeignKey(
                        name: "FK_nc_organization_bank_transaction_nc_organization_bank_account_organization_prototype_id1",
                        column: x => x.organization_prototype_id,
                        principalTable: "nc_organization_bank_account",
                        principalColumn: "organization_prototype_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_organization_bank_transaction_organization_prototype_id_created_at",
                table: "nc_organization_bank_transaction",
                columns: new[] { "organization_prototype_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_organization_bank_transaction");
        }
    }
}
