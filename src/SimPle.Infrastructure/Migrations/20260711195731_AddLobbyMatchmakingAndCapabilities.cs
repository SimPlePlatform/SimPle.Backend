using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimPle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLobbyMatchmakingAndCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_games_Slug",
                table: "games",
                column: "Slug");

            migrationBuilder.CreateTable(
                name: "capability_seed_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capability_seed_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "game_capability_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapabilityVersion = table.Column<int>(type: "integer", nullable: false),
                    MinPlayers = table.Column<int>(type: "integer", nullable: false),
                    MaxPlayers = table.Column<int>(type: "integer", nullable: false),
                    AllowedModes = table.Column<List<string>>(type: "text[]", nullable: false),
                    TimeControls = table.Column<List<string>>(type: "text[]", nullable: false),
                    TieBreakRules = table.Column<List<string>>(type: "text[]", nullable: false),
                    SpectatorPolicies = table.Column<List<string>>(type: "text[]", nullable: false),
                    RatedEligible = table.Column<bool>(type: "boolean", nullable: false),
                    AiFillEligible = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ManifestVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_capability_profiles", x => x.Id);
                    table.CheckConstraint("ck_game_capability_profiles_modes_nonempty", "cardinality(\"AllowedModes\") > 0");
                    table.CheckConstraint("ck_game_capability_profiles_players", "\"MinPlayers\" >= 2 AND \"MinPlayers\" <= \"MaxPlayers\" AND \"MaxPlayers\" <= 8");
                    table.CheckConstraint("ck_game_capability_profiles_spectators_nonempty", "cardinality(\"SpectatorPolicies\") > 0");
                    table.CheckConstraint("ck_game_capability_profiles_tie_breaks_nonempty", "cardinality(\"TieBreakRules\") > 0");
                    table.CheckConstraint("ck_game_capability_profiles_time_controls_nonempty", "cardinality(\"TimeControls\") > 0");
                    table.CheckConstraint("ck_game_capability_profiles_version", "\"CapabilityVersion\" >= 1");
                    table.ForeignKey(
                        name: "FK_game_capability_profiles_games_GameSlug",
                        column: x => x.GameSlug,
                        principalTable: "games",
                        principalColumn: "Slug",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lobbies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapabilityVersion = table.Column<int>(type: "integer", nullable: false),
                    HostUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Privacy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MaxPlayers = table.Column<int>(type: "integer", nullable: false),
                    TimeControlId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Rated = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedRegion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SpectatorPolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TieBreakRuleId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AiFillRequested = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedReason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lobbies", x => x.Id);
                    table.CheckConstraint("ck_lobbies_capability_version", "\"CapabilityVersion\" >= 1");
                    table.CheckConstraint("ck_lobbies_closed_reason_iff_terminal", "(\"State\" IN ('Closed', 'Expired')) = (\"ClosedReason\" IS NOT NULL)");
                    table.CheckConstraint("ck_lobbies_max_players", "\"MaxPlayers\" >= 2 AND \"MaxPlayers\" <= 8");
                    table.CheckConstraint("ck_lobbies_revision", "\"Revision\" >= 1");
                    table.ForeignKey(
                        name: "FK_lobbies_users_HostUserId",
                        column: x => x.HostUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "matchmaking_tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapabilityVersion = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlayerCount = table.Column<int>(type: "integer", nullable: false),
                    TimeControlId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Rated = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedRegion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    RatingSourceVersion = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EnqueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeadlineAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetryBudget = table.Column<int>(type: "integer", nullable: false),
                    ClaimedByWorker = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matchmaking_tickets", x => x.Id);
                    table.CheckConstraint("ck_matchmaking_tickets_capability_version", "\"CapabilityVersion\" >= 1");
                    table.CheckConstraint("ck_matchmaking_tickets_deadline_after_enqueue", "\"DeadlineAtUtc\" > \"EnqueuedAtUtc\"");
                    table.CheckConstraint("ck_matchmaking_tickets_no_worker_while_queued", "\"State\" <> 'Queued' OR \"ClaimedByWorker\" IS NULL");
                    table.CheckConstraint("ck_matchmaking_tickets_player_count", "\"PlayerCount\" >= 2 AND \"PlayerCount\" <= 8");
                    table.CheckConstraint("ck_matchmaking_tickets_retry_budget", "\"RetryBudget\" >= 0");
                    table.ForeignKey(
                        name: "FK_matchmaking_tickets_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lobby_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LobbyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InviterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InviteeUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lobby_invites", x => x.Id);
                    table.CheckConstraint("ck_lobby_invites_no_self_invite", "\"InviterUserId\" <> \"InviteeUserId\"");
                    table.CheckConstraint("ck_lobby_invites_responded_iff_terminal", "(\"State\" <> 'Pending') = (\"RespondedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_lobby_invites_lobbies_LobbyId",
                        column: x => x.LobbyId,
                        principalTable: "lobbies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_lobby_invites_users_InviteeUserId",
                        column: x => x.InviteeUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_lobby_invites_users_InviterUserId",
                        column: x => x.InviterUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lobby_join_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LobbyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LinkTokenDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SupersededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lobby_join_credentials", x => x.Id);
                    table.CheckConstraint("ck_lobby_join_credentials_generation", "\"Generation\" >= 1");
                    table.CheckConstraint("ck_lobby_join_credentials_superseded_iff_terminal", "(\"State\" <> 'Active') = (\"SupersededAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_lobby_join_credentials_lobbies_LobbyId",
                        column: x => x.LobbyId,
                        principalTable: "lobbies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lobby_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LobbyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsReady = table.Column<bool>(type: "boolean", nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeftAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lobby_members", x => x.Id);
                    table.CheckConstraint("ck_lobby_members_left_at_iff_terminal", "(\"State\" IN ('Left', 'Kicked')) = (\"LeftAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("ck_lobby_members_removed_by_only_on_kick", "\"RemovedByUserId\" IS NULL OR \"State\" = 'Kicked'");
                    table.ForeignKey(
                        name: "FK_lobby_members_lobbies_LobbyId",
                        column: x => x.LobbyId,
                        principalTable: "lobbies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_lobby_members_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lobby_start_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LobbyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LobbyRevision = table.Column<int>(type: "integer", nullable: false),
                    MatchRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lobby_start_requests", x => x.Id);
                    table.CheckConstraint("ck_lobby_start_requests_failure_reason_only_on_failed", "\"FailureReason\" IS NULL OR \"State\" = 'Failed'");
                    table.CheckConstraint("ck_lobby_start_requests_resolved_iff_terminal", "(\"State\" <> 'Open') = (\"ResolvedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("ck_lobby_start_requests_revision", "\"LobbyRevision\" >= 1");
                    table.ForeignKey(
                        name: "FK_lobby_start_requests_lobbies_LobbyId",
                        column: x => x.LobbyId,
                        principalTable: "lobbies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "matchmaking_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matchmaking_assignments", x => x.Id);
                    table.CheckConstraint("ck_matchmaking_assignments_resolved_iff_terminal", "(\"State\" <> 'Active') = (\"ResolvedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_matchmaking_assignments_matchmaking_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "matchmaking_tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_capability_seed_history_ManifestVersion",
                table: "capability_seed_history",
                column: "ManifestVersion",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_game_capability_profiles_one_active_per_game",
                table: "game_capability_profiles",
                column: "GameSlug",
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "ux_game_capability_profiles_pin",
                table: "game_capability_profiles",
                columns: new[] { "GameSlug", "CapabilityVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lobbies_expiry_sweep",
                table: "lobbies",
                column: "ExpiresAtUtc",
                filter: "\"State\" IN ('Open', 'Starting')");

            migrationBuilder.CreateIndex(
                name: "ix_lobbies_host",
                table: "lobbies",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "ix_lobbies_public_discovery",
                table: "lobbies",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"State\" = 'Open' AND \"Privacy\" = 'Public'");

            migrationBuilder.CreateIndex(
                name: "ix_lobby_invites_expiry_sweep",
                table: "lobby_invites",
                column: "ExpiresAtUtc",
                filter: "\"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_lobby_invites_invitee_pending",
                table: "lobby_invites",
                columns: new[] { "InviteeUserId", "CreatedAt", "Id" },
                filter: "\"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_lobby_invites_InviterUserId",
                table: "lobby_invites",
                column: "InviterUserId");

            migrationBuilder.CreateIndex(
                name: "ux_lobby_invites_one_pending_per_invitee",
                table: "lobby_invites",
                columns: new[] { "LobbyId", "InviteeUserId" },
                unique: true,
                filter: "\"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_lobby_join_credentials_active_code",
                table: "lobby_join_credentials",
                column: "CodeDigest",
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_lobby_join_credentials_active_link_token",
                table: "lobby_join_credentials",
                column: "LinkTokenDigest",
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_lobby_join_credentials_one_active_per_lobby",
                table: "lobby_join_credentials",
                column: "LobbyId",
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_lobby_members_lobby_tenure",
                table: "lobby_members",
                columns: new[] { "LobbyId", "JoinedAtUtc", "UserId" });

            migrationBuilder.CreateIndex(
                name: "ux_lobby_members_one_joined_per_user",
                table: "lobby_members",
                column: "UserId",
                unique: true,
                filter: "\"State\" = 'Joined'");

            migrationBuilder.CreateIndex(
                name: "ux_lobby_start_requests_idempotency",
                table: "lobby_start_requests",
                columns: new[] { "LobbyId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_lobby_start_requests_match_request",
                table: "lobby_start_requests",
                column: "MatchRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_lobby_start_requests_one_open_per_revision",
                table: "lobby_start_requests",
                columns: new[] { "LobbyId", "LobbyRevision" },
                unique: true,
                filter: "\"State\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_matchmaking_assignments_group",
                table: "matchmaking_assignments",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "ix_matchmaking_assignments_match_request",
                table: "matchmaking_assignments",
                column: "MatchRequestId");

            migrationBuilder.CreateIndex(
                name: "ux_matchmaking_assignments_one_active_per_ticket",
                table: "matchmaking_assignments",
                column: "TicketId",
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_matchmaking_tickets_candidate_pool",
                table: "matchmaking_tickets",
                columns: new[] { "GameSlug", "CapabilityVersion", "Mode", "PlayerCount", "TimeControlId", "Rated", "ResolvedRegion", "EnqueuedAtUtc", "Id" },
                filter: "\"State\" = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "ix_matchmaking_tickets_expiry_sweep",
                table: "matchmaking_tickets",
                column: "DeadlineAtUtc",
                filter: "\"State\" IN ('Queued', 'Claimed', 'Requeued')");

            migrationBuilder.CreateIndex(
                name: "ux_matchmaking_tickets_one_nonterminal_per_user",
                table: "matchmaking_tickets",
                column: "UserId",
                unique: true,
                filter: "\"State\" IN ('Queued', 'Claimed', 'Requeued')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capability_seed_history");

            migrationBuilder.DropTable(
                name: "game_capability_profiles");

            migrationBuilder.DropTable(
                name: "lobby_invites");

            migrationBuilder.DropTable(
                name: "lobby_join_credentials");

            migrationBuilder.DropTable(
                name: "lobby_members");

            migrationBuilder.DropTable(
                name: "lobby_start_requests");

            migrationBuilder.DropTable(
                name: "matchmaking_assignments");

            migrationBuilder.DropTable(
                name: "lobbies");

            migrationBuilder.DropTable(
                name: "matchmaking_tickets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_games_Slug",
                table: "games");
        }
    }
}
