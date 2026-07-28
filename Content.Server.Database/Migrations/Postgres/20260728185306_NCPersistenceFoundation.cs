using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NCPersistenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nc_bank_account",
                columns: table => new
                {
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    account_type = table.Column<byte>(type: "smallint", nullable: false),
                    owner_profile_id = table.Column<int>(type: "integer", nullable: true),
                    currency_prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    credential_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    credential_salt = table.Column<byte[]>(type: "bytea", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_bank_account", x => x.bank_account_id);
                    table.CheckConstraint("CK_nc_bank_account_balance", "balance >= 0");
                    table.CheckConstraint("CK_nc_bank_account_personal_owner", "(account_type IN (0, 3) AND owner_profile_id IS NOT NULL) OR (account_type NOT IN (0, 3) AND owner_profile_id IS NULL)");
                    table.CheckConstraint("CK_nc_bank_account_version", "version >= 0");
                    table.ForeignKey(
                        name: "FK_nc_bank_account_profile_profile_id",
                        column: x => x.owner_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nc_character_license",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    license_prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    issued_by_profile_id = table.Column<int>(type: "integer", nullable: true),
                    issued_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_license", x => new { x.profile_id, x.license_prototype_id });
                    table.ForeignKey(
                        name: "FK_nc_character_license_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_character_lifecycle",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    declared_round_id = table.Column<int>(type: "integer", nullable: true),
                    declared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    declared_by_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    declared_by_profile_id = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_lifecycle", x => x.profile_id);
                    table.ForeignKey(
                        name: "FK_nc_character_lifecycle_profile_profile_id1",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_character_progression",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    completed_rounds = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<byte>(type: "smallint", nullable: false),
                    spent_skill_points = table.Column<int>(type: "integer", nullable: false),
                    last_counted_round_id = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_progression", x => x.profile_id);
                    table.CheckConstraint("CK_nc_character_progression_completed_rounds", "completed_rounds >= 0");
                    table.CheckConstraint("CK_nc_character_progression_level", "level >= 1 AND level <= 10");
                    table.CheckConstraint("CK_nc_character_progression_spent_skill_points", "spent_skill_points >= 0 AND spent_skill_points <= level * 10");
                    table.ForeignKey(
                        name: "FK_nc_character_progression_profile_profile_id1",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_character_progression_round_round_id",
                        column: x => x.last_counted_round_id,
                        principalTable: "round",
                        principalColumn: "round_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "nc_character_round_participation",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active_seconds = table.Column<int>(type: "integer", nullable: false),
                    counted = table.Column<bool>(type: "boolean", nullable: false),
                    first_joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    counted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_round_participation", x => new { x.profile_id, x.round_id });
                    table.CheckConstraint("CK_nc_character_round_participation_active_seconds", "active_seconds >= 0");
                    table.ForeignKey(
                        name: "FK_nc_character_round_participation_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_character_round_participation_round_round_id",
                        column: x => x.round_id,
                        principalTable: "round",
                        principalColumn: "round_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_character_skill",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    skill_prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    spent_points = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_skill", x => new { x.profile_id, x.skill_prototype_id });
                    table.CheckConstraint("CK_nc_character_skill_rank", "rank >= 0");
                    table.CheckConstraint("CK_nc_character_skill_spent_points", "spent_points >= 0");
                    table.ForeignKey(
                        name: "FK_nc_character_skill_profile_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_deleted_character_audit",
                columns: table => new
                {
                    deleted_character_audit_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    deleted_profile_id = table.Column<int>(type: "integer", nullable: false),
                    last_character_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deletion_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_deleted_character_audit", x => x.deleted_character_audit_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_persistence_audit",
                columns: table => new
                {
                    audit_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_profile_id = table.Column<int>(type: "integer", nullable: true),
                    target_profile_id = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_persistence_audit", x => x.audit_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_property",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    map_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    property_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property", x => x.property_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_round_account_credit",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: false),
                    credited_profile_id = table.Column<int>(type: "integer", nullable: false),
                    credited_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_round_account_credit", x => new { x.account_id, x.round_id });
                    table.ForeignKey(
                        name: "FK_nc_round_account_credit_round_round_id",
                        column: x => x.round_id,
                        principalTable: "round",
                        principalColumn: "round_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_bank_transaction",
                columns: table => new
                {
                    bank_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debit_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency_prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    transaction_type = table.Column<byte>(type: "smallint", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_profile_id = table.Column<int>(type: "integer", nullable: true),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    debit_balance_after = table.Column<long>(type: "bigint", nullable: true),
                    credit_balance_after = table.Column<long>(type: "bigint", nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_bank_transaction", x => x.bank_transaction_id);
                    table.CheckConstraint("CK_nc_bank_transaction_accounts", "debit_account_id IS NOT NULL OR credit_account_id IS NOT NULL");
                    table.CheckConstraint("CK_nc_bank_transaction_amount", "amount > 0");
                    table.ForeignKey(
                        name: "FK_nc_bank_transaction_nc_bank_account_ncbank_account_bank_acc~",
                        column: x => x.credit_account_id,
                        principalTable: "nc_bank_account",
                        principalColumn: "bank_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_bank_transaction_nc_bank_account_ncbank_account_bank_ac~1",
                        column: x => x.debit_account_id,
                        principalTable: "nc_bank_account",
                        principalColumn: "bank_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nc_business",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    business_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_business", x => x.business_id);
                    table.ForeignKey(
                        name: "FK_nc_business_nc_bank_account_ncbank_account_bank_account_id",
                        column: x => x.bank_account_id,
                        principalTable: "nc_bank_account",
                        principalColumn: "bank_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_business_nc_property_ncproperty_property_id",
                        column: x => x.property_id,
                        principalTable: "nc_property",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nc_property_ownership",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_type = table.Column<byte>(type: "smallint", nullable: false),
                    owner_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    share_basis_points = table.Column<int>(type: "integer", nullable: false),
                    acquired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_property_ownership", x => new { x.property_id, x.owner_type, x.owner_id });
                    table.CheckConstraint("CK_nc_property_ownership_share", "share_basis_points > 0 AND share_basis_points <= 10000");
                    table.ForeignKey(
                        name: "FK_nc_property_ownership_nc_property_property_id",
                        column: x => x.property_id,
                        principalTable: "nc_property",
                        principalColumn: "property_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_business_ownership",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_profile_id = table.Column<int>(type: "integer", nullable: false),
                    share_basis_points = table.Column<int>(type: "integer", nullable: false),
                    ownership_type = table.Column<byte>(type: "smallint", nullable: false),
                    acquired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_business_ownership", x => new { x.business_id, x.owner_profile_id });
                    table.CheckConstraint("CK_nc_business_ownership_share", "share_basis_points > 0 AND share_basis_points <= 10000");
                    table.ForeignKey(
                        name: "FK_nc_business_ownership_nc_business_business_id",
                        column: x => x.business_id,
                        principalTable: "nc_business",
                        principalColumn: "business_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_nc_business_ownership_profile_profile_id",
                        column: x => x.owner_profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nc_character_employment",
                columns: table => new
                {
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employment_state = table.Column<byte>(type: "smallint", nullable: false),
                    hired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    hired_by_profile_id = table.Column<int>(type: "integer", nullable: true),
                    last_promotion_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    suspended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_character_employment", x => x.profile_id);
                    table.ForeignKey(
                        name: "FK_nc_character_employment_profile_profile_id1",
                        column: x => x.profile_id,
                        principalTable: "profile",
                        principalColumn: "profile_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nc_department",
                columns: table => new
                {
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_department", x => x.department_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_employment_history",
                columns: table => new
                {
                    employment_history_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    old_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    old_position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<byte>(type: "smallint", nullable: false),
                    actor_profile_id = table.Column<int>(type: "integer", nullable: true),
                    actor_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_employment_history", x => x.employment_history_id);
                });

            migrationBuilder.CreateTable(
                name: "nc_organization",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    default_entry_position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_organization", x => x.organization_id);
                    table.ForeignKey(
                        name: "FK_nc_organization_nc_bank_account_ncbank_account_bank_account~",
                        column: x => x.bank_account_id,
                        principalTable: "nc_bank_account",
                        principalColumn: "bank_account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "nc_position",
                columns: table => new
                {
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prototype_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    rank_weight = table.Column<int>(type: "integer", nullable: false),
                    base_salary = table.Column<long>(type: "bigint", nullable: false),
                    pay_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    payroll_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_leadership = table.Column<bool>(type: "boolean", nullable: false),
                    can_hire = table.Column<bool>(type: "boolean", nullable: false),
                    can_promote = table.Column<bool>(type: "boolean", nullable: false),
                    can_demote = table.Column<bool>(type: "boolean", nullable: false),
                    can_transfer = table.Column<bool>(type: "boolean", nullable: false),
                    can_suspend = table.Column<bool>(type: "boolean", nullable: false),
                    can_dismiss = table.Column<bool>(type: "boolean", nullable: false),
                    max_promotable_rank_weight = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nc_position", x => x.position_id);
                    table.CheckConstraint("CK_nc_position_base_salary", "base_salary >= 0");
                    table.CheckConstraint("CK_nc_position_pay_interval", "pay_interval_seconds >= 0");
                    table.ForeignKey(
                        name: "FK_nc_position_nc_bank_account_ncbank_account_bank_account_id",
                        column: x => x.payroll_account_id,
                        principalTable: "nc_bank_account",
                        principalColumn: "bank_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_position_nc_department_ncdepartment_department_id",
                        column: x => x.department_id,
                        principalTable: "nc_department",
                        principalColumn: "department_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nc_position_nc_organization_ncorganization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "nc_organization",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_account_account_number",
                table: "nc_bank_account",
                column: "account_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_account_owner_profile_id",
                table: "nc_bank_account",
                column: "owner_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_transaction_credit_account_id",
                table: "nc_bank_transaction",
                column: "credit_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_transaction_debit_account_id",
                table: "nc_bank_transaction",
                column: "debit_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_bank_transaction_request_id",
                table: "nc_bank_transaction",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_business_bank_account_id",
                table: "nc_business",
                column: "bank_account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_business_property_id",
                table: "nc_business",
                column: "property_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_business_ownership_owner_profile_id",
                table: "nc_business_ownership",
                column: "owner_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_employment_department_id",
                table: "nc_character_employment",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_employment_organization_id",
                table: "nc_character_employment",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_employment_position_id",
                table: "nc_character_employment",
                column: "position_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_lifecycle_request_id",
                table: "nc_character_lifecycle",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_progression_last_counted_round_id",
                table: "nc_character_progression",
                column: "last_counted_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_round_participation_account_id_round_id",
                table: "nc_character_round_participation",
                columns: new[] { "account_id", "round_id" });

            migrationBuilder.CreateIndex(
                name: "IX_nc_character_round_participation_round_id",
                table: "nc_character_round_participation",
                column: "round_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_deleted_character_audit_deleted_profile_id",
                table: "nc_deleted_character_audit",
                column: "deleted_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_deleted_character_audit_request_id",
                table: "nc_deleted_character_audit",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_department_organization_id_prototype_id",
                table: "nc_department",
                columns: new[] { "organization_id", "prototype_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_employment_history_organization_id",
                table: "nc_employment_history",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_employment_history_profile_id",
                table: "nc_employment_history",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_employment_history_request_id",
                table: "nc_employment_history",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_organization_bank_account_id",
                table: "nc_organization",
                column: "bank_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_organization_default_entry_position_id",
                table: "nc_organization",
                column: "default_entry_position_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_organization_prototype_id",
                table: "nc_organization",
                column: "prototype_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_persistence_audit_request_id",
                table: "nc_persistence_audit",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_persistence_audit_target_profile_id",
                table: "nc_persistence_audit",
                column: "target_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_persistence_audit_timestamp",
                table: "nc_persistence_audit",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_nc_position_department_id",
                table: "nc_position",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_position_organization_id_prototype_id",
                table: "nc_position",
                columns: new[] { "organization_id", "prototype_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_position_payroll_account_id",
                table: "nc_position",
                column: "payroll_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_property_map_entity_id",
                table: "nc_property",
                column: "map_entity_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nc_round_account_credit_credited_profile_id",
                table: "nc_round_account_credit",
                column: "credited_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_nc_round_account_credit_round_id",
                table: "nc_round_account_credit",
                column: "round_id");

            migrationBuilder.AddForeignKey(
                name: "FK_nc_character_employment_nc_department_ncdepartment_departme~",
                table: "nc_character_employment",
                column: "department_id",
                principalTable: "nc_department",
                principalColumn: "department_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_nc_character_employment_nc_organization_ncorganization_orga~",
                table: "nc_character_employment",
                column: "organization_id",
                principalTable: "nc_organization",
                principalColumn: "organization_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_nc_character_employment_nc_position_ncposition_position_id",
                table: "nc_character_employment",
                column: "position_id",
                principalTable: "nc_position",
                principalColumn: "position_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_nc_department_nc_organization_ncorganization_organization_id",
                table: "nc_department",
                column: "organization_id",
                principalTable: "nc_organization",
                principalColumn: "organization_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_nc_employment_history_nc_organization_ncorganization_organi~",
                table: "nc_employment_history",
                column: "organization_id",
                principalTable: "nc_organization",
                principalColumn: "organization_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_nc_organization_nc_position_ncposition_position_id",
                table: "nc_organization",
                column: "default_entry_position_id",
                principalTable: "nc_position",
                principalColumn: "position_id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_nc_organization_nc_bank_account_ncbank_account_bank_account~",
                table: "nc_organization");

            migrationBuilder.DropForeignKey(
                name: "FK_nc_position_nc_bank_account_ncbank_account_bank_account_id",
                table: "nc_position");

            migrationBuilder.DropForeignKey(
                name: "FK_nc_position_nc_department_ncdepartment_department_id",
                table: "nc_position");

            migrationBuilder.DropForeignKey(
                name: "FK_nc_position_nc_organization_ncorganization_organization_id",
                table: "nc_position");

            migrationBuilder.DropTable(
                name: "nc_bank_transaction");

            migrationBuilder.DropTable(
                name: "nc_business_ownership");

            migrationBuilder.DropTable(
                name: "nc_character_employment");

            migrationBuilder.DropTable(
                name: "nc_character_license");

            migrationBuilder.DropTable(
                name: "nc_character_lifecycle");

            migrationBuilder.DropTable(
                name: "nc_character_progression");

            migrationBuilder.DropTable(
                name: "nc_character_round_participation");

            migrationBuilder.DropTable(
                name: "nc_character_skill");

            migrationBuilder.DropTable(
                name: "nc_deleted_character_audit");

            migrationBuilder.DropTable(
                name: "nc_employment_history");

            migrationBuilder.DropTable(
                name: "nc_persistence_audit");

            migrationBuilder.DropTable(
                name: "nc_property_ownership");

            migrationBuilder.DropTable(
                name: "nc_round_account_credit");

            migrationBuilder.DropTable(
                name: "nc_business");

            migrationBuilder.DropTable(
                name: "nc_property");

            migrationBuilder.DropTable(
                name: "nc_bank_account");

            migrationBuilder.DropTable(
                name: "nc_department");

            migrationBuilder.DropTable(
                name: "nc_organization");

            migrationBuilder.DropTable(
                name: "nc_position");
        }
    }
}
