// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCAgentCitiNet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_agent_profile",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    address = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_profile", x => x.profile_id);
                    table.ForeignKey(
                        name: "FK_nc_agent_profile_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_contact",
                columns: table => new
                {
                    owner_profile_id = table.Column<int>(type: "integer", nullable: false),
                    contact_profile_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_contact", x => new { x.owner_profile_id, x.contact_profile_id });
                    table.ForeignKey(
                        name: "FK_nc_agent_contact_contact_profile_id",
                        column: x => x.contact_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_contact_owner_profile_id",
                        column: x => x.owner_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_message",
                columns: table => new
                {
                    nc_agent_message_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sender_profile_id = table.Column<int>(type: "integer", nullable: false),
                    recipient_profile_id = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_message", x => x.nc_agent_message_id);
                    table.ForeignKey(
                        name: "FK_nc_agent_message_recipient_profile_id",
                        column: x => x.recipient_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_message_sender_profile_id",
                        column: x => x.sender_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_citinet_post",
                columns: table => new
                {
                    nc_citinet_post_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    author_profile_id = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_citinet_post", x => x.nc_citinet_post_id);
                    table.ForeignKey(
                        name: "FK_nc_citinet_post_author_profile_id",
                        column: x => x.author_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_citinet_comment",
                columns: table => new
                {
                    nc_citinet_comment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    post_id = table.Column<long>(type: "bigint", nullable: false),
                    author_profile_id = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_citinet_comment", x => x.nc_citinet_comment_id);
                    table.ForeignKey(
                        name: "FK_nc_citinet_comment_author_profile_id",
                        column: x => x.author_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_citinet_comment_post_id",
                        column: x => x.post_id,
                        principalTable: "nc_citinet_post",
                        principalColumn: "nc_citinet_post_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_citinet_reaction",
                columns: table => new
                {
                    post_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_citinet_reaction", x => new { x.post_id, x.profile_id });
                    table.ForeignKey(
                        name: "FK_nc_citinet_reaction_post_id",
                        column: x => x.post_id,
                        principalTable: "nc_citinet_post",
                        principalColumn: "nc_citinet_post_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_citinet_reaction_profile_id",
                        column: x => x.profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_contact_contact_profile_id",
                table: "nc_agent_contact",
                column: "contact_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_message_expires_at",
                table: "nc_agent_message",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_message_recipient_profile_id",
                table: "nc_agent_message",
                column: "recipient_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_message_sender_profile_id_recipient_profile_id_sen~",
                table: "nc_agent_message",
                columns: new[] { "sender_profile_id", "recipient_profile_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_profile_address",
                table: "nc_agent_profile",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_citinet_comment_author_profile_id",
                table: "nc_citinet_comment",
                column: "author_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_citinet_comment_post_id_created_at",
                table: "nc_citinet_comment",
                columns: new[] { "post_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_citinet_post_author_profile_id",
                table: "nc_citinet_post",
                column: "author_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_citinet_post_created_at",
                table: "nc_citinet_post",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_citinet_reaction_profile_id",
                table: "nc_citinet_reaction",
                column: "profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_agent_contact");

            migrationBuilder.DropTable(
                name: "nc_agent_message");

            migrationBuilder.DropTable(
                name: "nc_citinet_comment");

            migrationBuilder.DropTable(
                name: "nc_citinet_reaction");

            migrationBuilder.DropTable(
                name: "nc_citinet_post");

            migrationBuilder.DropTable(
                name: "nc_agent_profile");
        }
    }
}
