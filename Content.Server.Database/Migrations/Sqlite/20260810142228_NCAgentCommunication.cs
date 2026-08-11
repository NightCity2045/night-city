// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NCAgentCommunication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "notification_sound_enabled",
                table: "nc_agent_profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "overlay_notifications_enabled",
                table: "nc_agent_profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<byte>(
                name: "presence",
                table: "nc_agent_profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "nc_agent_block",
                columns: table => new
                {
                    owner_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    blocked_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_block", x => new { x.owner_profile_id, x.blocked_profile_id });
                    table.ForeignKey(
                        name: "FK_nc_agent_block_blocked_profile_id",
                        column: x => x.blocked_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_block_owner_profile_id",
                        column: x => x.owner_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_direct_conversation_state",
                columns: table => new
                {
                    owner_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    peer_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    cleared_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_direct_conversation_state", x => new { x.owner_profile_id, x.peer_profile_id });
                    table.ForeignKey(
                        name: "FK_nc_agent_direct_state_owner_profile_id",
                        column: x => x.owner_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_direct_state_peer_profile_id",
                        column: x => x.peer_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_group",
                columns: table => new
                {
                    nc_agent_group_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    owner_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_group", x => x.nc_agent_group_id);
                    table.ForeignKey(
                        name: "FK_nc_agent_group_owner_profile_id",
                        column: x => x.owner_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_group_invite",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "INTEGER", nullable: false),
                    invitee_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    inviter_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_group_invite", x => new { x.group_id, x.invitee_profile_id });
                    table.ForeignKey(
                        name: "FK_nc_agent_group_invite_group_id",
                        column: x => x.group_id,
                        principalTable: "nc_agent_group",
                        principalColumn: "nc_agent_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_group_invite_invitee_profile_id",
                        column: x => x.invitee_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_group_invite_inviter_profile_id",
                        column: x => x.inviter_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_group_member",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "INTEGER", nullable: false),
                    profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    role = table.Column<byte>(type: "INTEGER", nullable: false),
                    joined_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_read_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    cleared_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_group_member", x => new { x.group_id, x.profile_id });
                    table.ForeignKey(
                        name: "FK_nc_agent_group_member_group_id",
                        column: x => x.group_id,
                        principalTable: "nc_agent_group",
                        principalColumn: "nc_agent_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_group_member_profile_id",
                        column: x => x.profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_group_message",
                columns: table => new
                {
                    nc_agent_group_message_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    group_id = table.Column<long>(type: "INTEGER", nullable: false),
                    sender_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    body = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    sent_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_group_message", x => x.nc_agent_group_message_id);
                    table.ForeignKey(
                        name: "FK_nc_agent_group_message_group_id",
                        column: x => x.group_id,
                        principalTable: "nc_agent_group",
                        principalColumn: "nc_agent_group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_group_message_sender_profile_id",
                        column: x => x.sender_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_block_blocked_profile_id",
                table: "nc_agent_block",
                column: "blocked_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_direct_conversation_state_peer_profile_id",
                table: "nc_agent_direct_conversation_state",
                column: "peer_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_owner_profile_id",
                table: "nc_agent_group",
                column: "owner_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_updated_at",
                table: "nc_agent_group",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_invite_expires_at",
                table: "nc_agent_group_invite",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_invite_invitee_profile_id",
                table: "nc_agent_group_invite",
                column: "invitee_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_invite_inviter_profile_id",
                table: "nc_agent_group_invite",
                column: "inviter_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_member_profile_id",
                table: "nc_agent_group_member",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_message_expires_at",
                table: "nc_agent_group_message",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_message_group_id_sent_at",
                table: "nc_agent_group_message",
                columns: new[] { "group_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_group_message_sender_profile_id",
                table: "nc_agent_group_message",
                column: "sender_profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_agent_block");

            migrationBuilder.DropTable(
                name: "nc_agent_direct_conversation_state");

            migrationBuilder.DropTable(
                name: "nc_agent_group_invite");

            migrationBuilder.DropTable(
                name: "nc_agent_group_member");

            migrationBuilder.DropTable(
                name: "nc_agent_group_message");

            migrationBuilder.DropTable(
                name: "nc_agent_group");

            migrationBuilder.DropColumn(
                name: "notification_sound_enabled",
                table: "nc_agent_profile");

            migrationBuilder.DropColumn(
                name: "overlay_notifications_enabled",
                table: "nc_agent_profile");

            migrationBuilder.DropColumn(
                name: "presence",
                table: "nc_agent_profile");
        }
    }
}
