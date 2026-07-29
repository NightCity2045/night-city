using System;
// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCLegalOwnershipAndInheritance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "nc_character_license",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "revoked_at",
                table: "nc_character_license",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "status",
                table: "nc_character_license",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "nc_character_document",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    document_prototype_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    serial_number = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<byte>(type: "INTEGER", nullable: false),
                    issued_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    issued_by_profile_id = table.Column<int>(type: "INTEGER", nullable: true),
                    issued_by_admin_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    payload = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_document", x => x.document_id);
                    table.ForeignKey(
                        name: "FK_nc_character_document_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_inheritance_case",
                columns: table => new
                {
                    inheritance_case_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    asset_type = table.Column<byte>(type: "INTEGER", nullable: false),
                    asset_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    deceased_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    share_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<byte>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    resolved_owner_type = table.Column<byte>(type: "INTEGER", nullable: true),
                    resolved_owner_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    resolved_by_account_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_inheritance_case", x => x.inheritance_case_id);
                    table.CheckConstraint("CK_nc_inheritance_case_share", "share_basis_points > 0 AND share_basis_points <= 10000");
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_document_profile_id_document_prototype_id",
                table: "nc_character_document",
                columns: new[] { "profile_id", "document_prototype_id" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_document_serial_number",
                table: "nc_character_document",
                column: "serial_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_inheritance_case_asset_type_asset_id_status",
                table: "nc_inheritance_case",
                columns: new[] { "asset_type", "asset_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_character_document");

            migrationBuilder.DropTable(
                name: "nc_inheritance_case");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "nc_character_license");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "nc_character_license");

            migrationBuilder.DropColumn(
                name: "status",
                table: "nc_character_license");
        }
    }
}
