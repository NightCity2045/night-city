using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCRemoveAgentAndMeta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nc_agent_block");

            migrationBuilder.DropTable(
                name: "nc_agent_call_log");

            migrationBuilder.DropTable(
                name: "nc_agent_contact");

            migrationBuilder.DropTable(
                name: "nc_agent_direct_conversation_state");

            migrationBuilder.DropTable(
                name: "nc_agent_group_invite");

            migrationBuilder.DropTable(
                name: "nc_agent_group_member");

            migrationBuilder.DropTable(
                name: "nc_agent_group_message");

            migrationBuilder.DropTable(
                name: "nc_agent_message");

            migrationBuilder.DropTable(
                name: "nc_citinet_comment");

            migrationBuilder.DropTable(
                name: "nc_citinet_reaction");

            migrationBuilder.DropTable(
                name: "nc_meta_blob_maintenance");

            migrationBuilder.DropTable(
                name: "nc_meta_blob_reference");

            migrationBuilder.DropTable(
                name: "nc_meta_blob_storage");

            migrationBuilder.DropTable(
                name: "nc_meta_checkpoint_blob_backfill");

            migrationBuilder.DropTable(
                name: "nc_meta_checkpoint_blob_reference");

            migrationBuilder.DropTable(
                name: "nc_agent_group");

            migrationBuilder.DropTable(
                name: "nc_citinet_post");

            migrationBuilder.DropTable(
                name: "nc_meta_revision");

            migrationBuilder.DropTable(
                name: "nc_meta_blob");

            migrationBuilder.DropTable(
                name: "nc_agent_profile");

            migrationBuilder.DropIndex(
                name: "IX_nc_aggregate_snapshot_aggregate_type_aggregate_version_crea~",
                table: "nc_aggregate_snapshot");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_agent_profile",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    address = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    alias = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    normalized_alias = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    notification_sound_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    overlay_notifications_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    presence = table.Column<byte>(type: "smallint", nullable: false),
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
                name: "nc_meta_blob",
                columns: table => new
                {
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    admitted_until_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    byte_count = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob", x => x.digest);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_blob_maintenance",
                columns: table => new
                {
                    nc_meta_blob_maintenance_id = table.Column<byte>(type: "smallint", nullable: false),
                    aggregate_id_cursor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    aggregate_type_cursor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregates_complete = table.Column<bool>(type: "boolean", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob_maintenance", x => x.nc_meta_blob_maintenance_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_blob_storage",
                columns: table => new
                {
                    nc_meta_blob_storage_id = table.Column<byte>(type: "smallint", nullable: false),
                    allocated_bytes = table.Column<long>(type: "bigint", nullable: false),
                    allocated_count = table.Column<long>(type: "bigint", nullable: false),
                    backfill_complete = table.Column<bool>(type: "boolean", nullable: false),
                    backfill_cursor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    byte_tokens = table.Column<double>(type: "double precision", nullable: false),
                    count_tokens = table.Column<double>(type: "double precision", nullable: false),
                    refilled_at_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob_storage", x => x.nc_meta_blob_storage_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_checkpoint_blob_backfill",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    complete = table.Column<bool>(type: "boolean", nullable: false),
                    digest_cursor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    next_chunk = table.Column<int>(type: "integer", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_checkpoint_blob_backfill", x => x.snapshot_id);
                    table.ForeignKey(
                        name: "FK_nc_meta_checkpoint_blob_backfill_nc_round_snapshot_ncround_~",
                        column: x => x.snapshot_id,
                        principalTable: "nc_round_snapshot",
                        principalColumn: "snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_revision",
                columns: table => new
                {
                    aggregate_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    active_world_ticks = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    reference_policy_version = table.Column<int>(type: "integer", nullable: false),
                    session_epoch = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_revision", x => new { x.aggregate_type, x.aggregate_id, x.aggregate_version });
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_block",
                columns: table => new
                {
                    owner_profile_id = table.Column<int>(type: "integer", nullable: false),
                    blocked_profile_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "nc_agent_call_log",
                columns: table => new
                {
                    nc_agent_call_log_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    caller_profile_id = table.Column<int>(type: "integer", nullable: false),
                    target_profile_id = table.Column<int>(type: "integer", nullable: false),
                    answered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<byte>(type: "smallint", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    target_read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_agent_call_log", x => x.nc_agent_call_log_id);
                    table.ForeignKey(
                        name: "FK_nc_agent_call_log_caller_profile_id",
                        column: x => x.caller_profile_id,
                        principalTable: "nc_agent_profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_agent_call_log_target_profile_id",
                        column: x => x.target_profile_id,
                        principalTable: "nc_agent_profile",
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
                name: "nc_agent_direct_conversation_state",
                columns: table => new
                {
                    owner_profile_id = table.Column<int>(type: "integer", nullable: false),
                    peer_profile_id = table.Column<int>(type: "integer", nullable: false),
                    cleared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    nc_agent_group_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_profile_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    name = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "nc_agent_message",
                columns: table => new
                {
                    nc_agent_message_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipient_profile_id = table.Column<int>(type: "integer", nullable: false),
                    sender_profile_id = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "nc_meta_checkpoint_blob_reference",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_checkpoint_blob_reference", x => new { x.snapshot_id, x.digest });
                    table.ForeignKey(
                        name: "FK_nc_meta_checkpoint_blob_reference_nc_meta_blob_digest",
                        column: x => x.digest,
                        principalTable: "nc_meta_blob",
                        principalColumn: "digest",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_meta_checkpoint_blob_reference_nc_round_snapshot_snapsho~",
                        column: x => x.snapshot_id,
                        principalTable: "nc_round_snapshot",
                        principalColumn: "snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_meta_blob_reference",
                columns: table => new
                {
                    aggregate_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_meta_blob_reference", x => new { x.aggregate_type, x.aggregate_id, x.aggregate_version, x.digest });
                    table.ForeignKey(
                        name: "FK_nc_meta_blob_reference_nc_meta_blob_digest",
                        column: x => x.digest,
                        principalTable: "nc_meta_blob",
                        principalColumn: "digest",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_meta_blob_reference_nc_meta_revision_aggregate_type_aggr~",
                        columns: x => new { x.aggregate_type, x.aggregate_id, x.aggregate_version },
                        principalTable: "nc_meta_revision",
                        principalColumns: new[] { "aggregate_type", "aggregate_id", "aggregate_version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_agent_group_invite",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    invitee_profile_id = table.Column<int>(type: "integer", nullable: false),
                    inviter_profile_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    cleared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    role = table.Column<byte>(type: "smallint", nullable: false)
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
                    nc_agent_group_message_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    sender_profile_id = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "nc_citinet_comment",
                columns: table => new
                {
                    nc_citinet_comment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    author_profile_id = table.Column<int>(type: "integer", nullable: false),
                    post_id = table.Column<long>(type: "bigint", nullable: false),
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

            migrationBuilder.InsertData(
                table: "nc_meta_blob_storage",
                columns: new[] { "nc_meta_blob_storage_id", "allocated_bytes", "allocated_count", "backfill_complete", "backfill_cursor", "byte_tokens", "count_tokens", "refilled_at_utc_ticks" },
                values: new object[] { (byte)1, 0L, 0L, false, "", 0.0, 0.0, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_nc_aggregate_snapshot_aggregate_type_aggregate_version_crea~",
                table: "nc_aggregate_snapshot",
                columns: new[] { "aggregate_type", "aggregate_version", "created_at", "aggregate_id" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_block_blocked_profile_id",
                table: "nc_agent_block",
                column: "blocked_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_call_log_caller_profile_id",
                table: "nc_agent_call_log",
                column: "caller_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_call_log_ended_at",
                table: "nc_agent_call_log",
                column: "ended_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_call_log_target_profile_id_target_read_at",
                table: "nc_agent_call_log",
                columns: new[] { "target_profile_id", "target_read_at" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_agent_contact_contact_profile_id",
                table: "nc_agent_contact",
                column: "contact_profile_id");

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
                name: "IX_nc_agent_profile_normalized_alias",
                table: "nc_agent_profile",
                column: "normalized_alias",
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

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_blob_created_at",
                table: "nc_meta_blob",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_blob_reference_digest",
                table: "nc_meta_blob_reference",
                column: "digest");

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_checkpoint_blob_reference_digest",
                table: "nc_meta_checkpoint_blob_reference",
                column: "digest");

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_revision_created_at",
                table: "nc_meta_revision",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_nc_meta_revision_reference_policy_version_aggregate_type_ag~",
                table: "nc_meta_revision",
                columns: new[] { "reference_policy_version", "aggregate_type", "aggregate_id", "aggregate_version" });
        }
    }
}
