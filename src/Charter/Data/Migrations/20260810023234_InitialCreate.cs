using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Charter.Data.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "jobs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "text", nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                claimed_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false),
                max_attempts = table.Column<int>(type: "integer", nullable: false),
                priority = table.Column<int>(type: "integer", nullable: false),
                required_capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                last_error = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                version = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_jobs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "organizations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                mode = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_organizations", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                teaching_level = table.Column<string>(type: "text", nullable: false),
                theme = table.Column<string>(type: "text", nullable: false),
                pane = table.Column<string>(type: "text", nullable: true),
                requester_onboarding_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "budgets",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                scope_type = table.Column<string>(type: "text", nullable: false),
                scope_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                unit = table.Column<string>(type: "text", nullable: false),
                categories = table.Column<string[]>(type: "text[]", nullable: false),
                period = table.Column<string>(type: "text", nullable: false),
                period_anchor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                amount = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                behaviour = table.Column<string>(type: "text", nullable: false),
                approval_threshold = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                rollover = table.Column<string>(type: "text", nullable: false),
                rollover_cap = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                reserved_amount = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                alert_thresholds = table.Column<double[]>(type: "double precision[]", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_budgets", x => x.id);
                table.ForeignKey(
                    name: "fk_budgets_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "repos",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                github_installation_id = table.Column<long>(type: "bigint", nullable: false),
                full_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                base_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                charter_config_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                primer_md = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_repos", x => x.id);
                table.ForeignKey(
                    name: "fk_repos_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "audit_logs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                target_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                target_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                metadata = table.Column<string>(type: "jsonb", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_audit_logs", x => x.id);
                table.ForeignKey(
                    name: "fk_audit_logs_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_audit_logs_users_actor_user_id",
                    column: x => x.actor_user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "concept_ledger",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                concept = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                first_explained_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_referenced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                times_referenced = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_concept_ledger", x => x.id);
                table.ForeignKey(
                    name: "fk_concept_ledger_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "credential_grants",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                scope = table.Column<string>(type: "text", nullable: false),
                secret_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                refresh_token_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                exhausted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                invalid_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                overflow_enabled = table.Column<bool>(type: "boolean", nullable: false),
                overflow_status = table.Column<string>(type: "text", nullable: false),
                overflow_exhausted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                priority = table.Column<int>(type: "integer", nullable: false),
                max_sessions_per_day_from_others = table.Column<int>(type: "integer", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_credential_grants", x => x.id);
                table.ForeignKey(
                    name: "fk_credential_grants_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_credential_grants_users_owner_user_id",
                    column: x => x.owner_user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "identities",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                provider = table.Column<string>(type: "text", nullable: false),
                provider_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                secret_hash = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_identities", x => x.id);
                table.ForeignKey(
                    name: "fk_identities_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "members",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                roles = table.Column<string[]>(type: "text[]", nullable: false),
                capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_members", x => x.id);
                table.ForeignKey(
                    name: "fk_members_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_members_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "auto_dispatch_policies",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                repo_id = table.Column<Guid>(type: "uuid", nullable: true),
                role = table.Column<string>(type: "text", nullable: true),
                user_id = table.Column<Guid>(type: "uuid", nullable: true),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                max_cost_usd = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                max_concurrent_sessions = table.Column<int>(type: "integer", nullable: true),
                allowed_paths = table.Column<string[]>(type: "text[]", nullable: false),
                project_types = table.Column<string[]>(type: "text[]", nullable: false),
                require_approval_above_usd = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_auto_dispatch_policies", x => x.id);
                table.ForeignKey(
                    name: "fk_auto_dispatch_policies_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_auto_dispatch_policies_repos_repo_id",
                    column: x => x.repo_id,
                    principalTable: "repos",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_auto_dispatch_policies_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "requests",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                repo_id = table.Column<Guid>(type: "uuid", nullable: false),
                requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                raw_text = table.Column<string>(type: "text", nullable: false),
                template_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_requests", x => x.id);
                table.ForeignKey(
                    name: "fk_requests_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_requests_repos_repo_id",
                    column: x => x.repo_id,
                    principalTable: "repos",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_requests_users_requester_id",
                    column: x => x.requester_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "repo_scopes",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                repo_id = table.Column<Guid>(type: "uuid", nullable: false),
                member_id = table.Column<Guid>(type: "uuid", nullable: true),
                role = table.Column<string>(type: "text", nullable: true),
                can_request = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_repo_scopes", x => x.id);
                table.CheckConstraint("ck_repo_scopes_member_xor_role", "(member_id IS NULL) <> (role IS NULL)");
                table.ForeignKey(
                    name: "fk_repo_scopes_members_member_id",
                    column: x => x.member_id,
                    principalTable: "members",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_repo_scopes_repos_repo_id",
                    column: x => x.repo_id,
                    principalTable: "repos",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "conversations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                request_id = table.Column<Guid>(type: "uuid", nullable: true),
                mode = table.Column<string>(type: "text", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                flags = table.Column<string>(type: "jsonb", nullable: true),
                flag_count = table.Column<int>(type: "integer", nullable: false),
                flags_cleared = table.Column<bool>(type: "boolean", nullable: false),
                spec = table.Column<string>(type: "jsonb", nullable: true),
                confirmed_content_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_conversations", x => x.id);
                table.ForeignKey(
                    name: "fk_conversations_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_conversations_requests_request_id",
                    column: x => x.request_id,
                    principalTable: "requests",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_conversations_users_confirmed_by",
                    column: x => x.confirmed_by,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "specs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                request_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                outcome = table.Column<string>(type: "text", nullable: false),
                body_md = table.Column<string>(type: "text", nullable: false),
                acceptance_criteria = table.Column<string>(type: "jsonb", nullable: false),
                technical_approach = table.Column<string>(type: "text", nullable: true),
                scope = table.Column<string>(type: "jsonb", nullable: true),
                risks = table.Column<string>(type: "jsonb", nullable: true),
                open_questions = table.Column<string>(type: "jsonb", nullable: true),
                approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_specs", x => x.id);
                table.ForeignKey(
                    name: "fk_specs_requests_request_id",
                    column: x => x.request_id,
                    principalTable: "requests",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_specs_users_approved_by",
                    column: x => x.approved_by,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "conversation_turns",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                seq = table.Column<int>(type: "integer", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                mode = table.Column<string>(type: "text", nullable: false),
                at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                body = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_conversation_turns", x => x.id);
                table.ForeignKey(
                    name: "fk_conversation_turns_conversations_conversation_id",
                    column: x => x.conversation_id,
                    principalTable: "conversations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "sessions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                spec_id = table.Column<Guid>(type: "uuid", nullable: false),
                runner = table.Column<string>(type: "text", nullable: false),
                agent_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                base_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                auto_dispatched = table.Column<bool>(type: "boolean", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                cost_usd = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_sessions", x => x.id);
                table.ForeignKey(
                    name: "fk_sessions_specs_spec_id",
                    column: x => x.spec_id,
                    principalTable: "specs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                seq = table.Column<long>(type: "bigint", nullable: false),
                type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_events", x => x.id);
                table.ForeignKey(
                    name: "fk_events_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ledger_entries",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: true),
                budget_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                category = table.Column<string>(type: "text", nullable: false),
                unit = table.Column<string>(type: "text", nullable: false),
                usd = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                quota_sessions = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                imputed_usd = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                state = table.Column<string>(type: "text", nullable: false),
                reserved_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                credential_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ledger_entries", x => x.id);
                table.ForeignKey(
                    name: "fk_ledger_entries_credential_grants_credential_grant_id",
                    column: x => x.credential_grant_id,
                    principalTable: "credential_grants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_ledger_entries_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_ledger_entries_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_ledger_entries_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "change_requests",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                number = table.Column<int>(type: "integer", nullable: false),
                url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                head_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                state = table.Column<string>(type: "text", nullable: false),
                is_stale = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_change_requests", x => x.id);
                table.ForeignKey(
                    name: "fk_change_requests_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "recaps",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                body_md = table.Column<string>(type: "text", nullable: false),
                risk_items = table.Column<string>(type: "jsonb", nullable: false),
                cost_usd = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_recaps", x => x.id);
                table.ForeignKey(
                    name: "fk_recaps_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "request_feedback",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                request_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: true),
                submitted_by = table.Column<Guid>(type: "uuid", nullable: false),
                verdict = table.Column<string>(type: "text", nullable: false),
                note = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_request_feedback", x => x.id);
                table.ForeignKey(
                    name: "fk_request_feedback_requests_request_id",
                    column: x => x.request_id,
                    principalTable: "requests",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_request_feedback_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_request_feedback_users_submitted_by",
                    column: x => x.submitted_by,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "verification_artifacts",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                state = table.Column<string>(type: "text", nullable: false),
                url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                file_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                connect_string = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                payload = table.Column<string>(type: "jsonb", nullable: true),
                instructions_md = table.Column<string>(type: "text", nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                audience = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_verification_artifacts", x => x.id);
                table.ForeignKey(
                    name: "fk_verification_artifacts_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "walkthroughs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                level = table.Column<string>(type: "text", nullable: false),
                body_md = table.Column<string>(type: "text", nullable: false),
                cost_usd = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_walkthroughs", x => x.id);
                table.ForeignKey(
                    name: "fk_walkthroughs_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "milestones",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                label = table.Column<string>(type: "text", nullable: false),
                annotation_md = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_milestones", x => x.id);
                table.ForeignKey(
                    name: "fk_milestones_events_event_id",
                    column: x => x.event_id,
                    principalTable: "events",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_milestones_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "deployments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                change_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                state = table.Column<string>(type: "text", nullable: false),
                reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_deployments", x => x.id);
                table.ForeignKey(
                    name: "fk_deployments_change_requests_change_request_id",
                    column: x => x.change_request_id,
                    principalTable: "change_requests",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "runner_agents",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                org_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                mode = table.Column<string>(type: "text", nullable: false),
                agent_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                protocol_version = table.Column<int>(type: "integer", nullable: false),
                capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                capabilities_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                capabilities_probed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                concurrency = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                credential_hash = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                pairing_token_hash = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                pairing_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                os = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                arch = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                rid = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                cpu_count = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                paired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                revoked_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                version = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_runner_agents", x => x.id);
                table.ForeignKey(
                    name: "fk_runner_agents_organizations_org_id",
                    column: x => x.org_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_runner_agents_capabilities",
            table: "runner_agents",
            column: "capabilities")
            .Annotation("Npgsql:IndexMethod", "gin");

        migrationBuilder.CreateIndex(
            name: "ix_runner_agents_org_id_name",
            table: "runner_agents",
            columns: new[] { "org_id", "name" });

        migrationBuilder.CreateIndex(
            name: "ix_runner_agents_pairing_token_expires_at",
            table: "runner_agents",
            column: "pairing_token_expires_at",
            filter: "pairing_token_hash IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_runner_agents_status",
            table: "runner_agents",
            column: "status");

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_actor_user_id",
            table: "audit_logs",
            column: "actor_user_id");

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_org_id_created_at",
            table: "audit_logs",
            columns: new[] { "org_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_target_type_target_id",
            table: "audit_logs",
            columns: new[] { "target_type", "target_id" });

        migrationBuilder.CreateIndex(
            name: "ix_auto_dispatch_policies_org_id_enabled",
            table: "auto_dispatch_policies",
            columns: new[] { "org_id", "enabled" });

        migrationBuilder.CreateIndex(
            name: "ix_auto_dispatch_policies_repo_id",
            table: "auto_dispatch_policies",
            column: "repo_id");

        migrationBuilder.CreateIndex(
            name: "ix_auto_dispatch_policies_user_id",
            table: "auto_dispatch_policies",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_budgets_org_id_scope_type_scope_id",
            table: "budgets",
            columns: new[] { "org_id", "scope_type", "scope_id" });

        migrationBuilder.CreateIndex(
            name: "ix_concept_ledger_user_id_last_referenced_at",
            table: "concept_ledger",
            columns: new[] { "user_id", "last_referenced_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ux_concept_ledger_user_id_concept",
            table: "concept_ledger",
            columns: new[] { "user_id", "concept" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_conversation_turns_conversation_id_seq",
            table: "conversation_turns",
            columns: new[] { "conversation_id", "seq" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_conversations_confirmed_by",
            table: "conversations",
            column: "confirmed_by");

        migrationBuilder.CreateIndex(
            name: "ix_conversations_org_id_updated_at",
            table: "conversations",
            columns: new[] { "org_id", "updated_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_conversations_request_id",
            table: "conversations",
            column: "request_id");

        migrationBuilder.CreateIndex(
            name: "ix_credential_grants_org_id_status_priority",
            table: "credential_grants",
            columns: new[] { "org_id", "status", "priority" },
            descending: new[] { false, false, true });

        migrationBuilder.CreateIndex(
            name: "ix_credential_grants_owner_user_id",
            table: "credential_grants",
            column: "owner_user_id");

        migrationBuilder.CreateIndex(
            name: "ix_deployments_change_request_id_reported_at",
            table: "deployments",
            columns: new[] { "change_request_id", "reported_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_events_created_at",
            table: "events",
            column: "created_at");

        migrationBuilder.CreateIndex(
            name: "ux_events_session_id_seq",
            table: "events",
            columns: new[] { "session_id", "seq" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_identities_user_id",
            table: "identities",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ux_identities_provider_provider_user_id",
            table: "identities",
            columns: new[] { "provider", "provider_user_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_jobs_claimable",
            table: "jobs",
            columns: new[] { "status", "priority", "available_at" },
            descending: new[] { false, true, false },
            filter: "status = 'pending'");

        migrationBuilder.CreateIndex(
            name: "ix_jobs_claimed_by",
            table: "jobs",
            column: "claimed_by",
            filter: "status = 'claimed'");

        migrationBuilder.CreateIndex(
            name: "ix_jobs_lease_expires_at",
            table: "jobs",
            column: "lease_expires_at",
            filter: "status = 'claimed'");

        migrationBuilder.CreateIndex(
            name: "ix_jobs_required_capabilities",
            table: "jobs",
            column: "required_capabilities")
            .Annotation("Npgsql:IndexMethod", "gin");

        migrationBuilder.CreateIndex(
            name: "ix_ledger_entries_budget_ids",
            table: "ledger_entries",
            column: "budget_ids")
            .Annotation("Npgsql:IndexMethod", "gin");

        migrationBuilder.CreateIndex(
            name: "ix_ledger_entries_credential_grant_id",
            table: "ledger_entries",
            column: "credential_grant_id");

        migrationBuilder.CreateIndex(
            name: "ix_ledger_entries_org_id_created_at",
            table: "ledger_entries",
            columns: new[] { "org_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_ledger_entries_reserved_until",
            table: "ledger_entries",
            column: "reserved_until",
            filter: "state = 'reserved'");

        migrationBuilder.CreateIndex(
            name: "ix_ledger_entries_session_id",
            table: "ledger_entries",
            column: "session_id");

        migrationBuilder.CreateIndex(
            name: "ix_ledger_entries_user_id_created_at",
            table: "ledger_entries",
            columns: new[] { "user_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_members_roles",
            table: "members",
            column: "roles")
            .Annotation("Npgsql:IndexMethod", "gin");

        migrationBuilder.CreateIndex(
            name: "ix_members_user_id",
            table: "members",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ux_members_org_id_user_id",
            table: "members",
            columns: new[] { "org_id", "user_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_milestones_session_id_created_at",
            table: "milestones",
            columns: new[] { "session_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ux_milestones_event_id",
            table: "milestones",
            column: "event_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_change_requests_head_sha",
            table: "change_requests",
            column: "head_sha");

        migrationBuilder.CreateIndex(
            name: "ux_change_requests_session_id_number",
            table: "change_requests",
            columns: new[] { "session_id", "number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_recaps_session_id",
            table: "recaps",
            column: "session_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_repo_scopes_member_id",
            table: "repo_scopes",
            column: "member_id");

        migrationBuilder.CreateIndex(
            name: "ux_repo_scopes_repo_id_member_id",
            table: "repo_scopes",
            columns: new[] { "repo_id", "member_id" },
            unique: true,
            filter: "member_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ux_repo_scopes_repo_id_role",
            table: "repo_scopes",
            columns: new[] { "repo_id", "role" },
            unique: true,
            filter: "role IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_repos_github_installation_id",
            table: "repos",
            column: "github_installation_id");

        migrationBuilder.CreateIndex(
            name: "ux_repos_org_id_full_name",
            table: "repos",
            columns: new[] { "org_id", "full_name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_request_feedback_request_id_created_at",
            table: "request_feedback",
            columns: new[] { "request_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_request_feedback_session_id",
            table: "request_feedback",
            column: "session_id");

        migrationBuilder.CreateIndex(
            name: "ix_request_feedback_submitted_by",
            table: "request_feedback",
            column: "submitted_by");

        migrationBuilder.CreateIndex(
            name: "ix_requests_org_id_created_at",
            table: "requests",
            columns: new[] { "org_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_requests_repo_id_status",
            table: "requests",
            columns: new[] { "repo_id", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_requests_requester_id_created_at",
            table: "requests",
            columns: new[] { "requester_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_sessions_spec_id",
            table: "sessions",
            column: "spec_id");

        migrationBuilder.CreateIndex(
            name: "ix_sessions_status_created_at",
            table: "sessions",
            columns: new[] { "status", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_specs_approved_by",
            table: "specs",
            column: "approved_by");

        migrationBuilder.CreateIndex(
            name: "ux_specs_request_id_version",
            table: "specs",
            columns: new[] { "request_id", "version" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_users_email",
            table: "users",
            column: "email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_verification_artifacts_expires_at",
            table: "verification_artifacts",
            column: "expires_at",
            filter: "expires_at IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_verification_artifacts_session_id",
            table: "verification_artifacts",
            column: "session_id");

        migrationBuilder.CreateIndex(
            name: "ux_walkthroughs_session_id_level",
            table: "walkthroughs",
            columns: new[] { "session_id", "level" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_logs");

        migrationBuilder.DropTable(
            name: "auto_dispatch_policies");

        migrationBuilder.DropTable(
            name: "budgets");

        migrationBuilder.DropTable(
            name: "concept_ledger");

        migrationBuilder.DropTable(
            name: "conversation_turns");

        migrationBuilder.DropTable(
            name: "deployments");

        migrationBuilder.DropTable(
            name: "identities");

        migrationBuilder.DropTable(
            name: "jobs");

        migrationBuilder.DropTable(
            name: "ledger_entries");

        migrationBuilder.DropTable(
            name: "milestones");

        migrationBuilder.DropTable(
            name: "recaps");

        migrationBuilder.DropTable(
            name: "repo_scopes");

        migrationBuilder.DropTable(
            name: "request_feedback");

        migrationBuilder.DropTable(
            name: "verification_artifacts");

        migrationBuilder.DropTable(
            name: "walkthroughs");

        migrationBuilder.DropTable(
            name: "conversations");

        migrationBuilder.DropTable(
            name: "change_requests");

        migrationBuilder.DropTable(
            name: "credential_grants");

        migrationBuilder.DropTable(
            name: "events");

        migrationBuilder.DropTable(
            name: "members");

        migrationBuilder.DropTable(
            name: "sessions");

        migrationBuilder.DropTable(
            name: "specs");

        migrationBuilder.DropTable(
            name: "requests");

        migrationBuilder.DropTable(
            name: "repos");

        migrationBuilder.DropTable(
            name: "users");

        migrationBuilder.DropTable(
            name: "runner_agents");

        migrationBuilder.DropTable(
            name: "organizations");
    }
}
