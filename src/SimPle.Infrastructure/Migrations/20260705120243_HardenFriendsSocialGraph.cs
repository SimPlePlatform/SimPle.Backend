using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimPle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenFriendsSocialGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_friendships_AddresseeId_Status",
                table: "friendships");

            migrationBuilder.DropIndex(
                name: "IX_friendships_RequesterId_Status",
                table: "friendships");

            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedAt",
                table: "friendships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DomainVersion",
                table: "friendships",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "friendships",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndedAt",
                table: "friendships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRequestAllowedAt",
                table: "friendships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestCycleId",
                table: "friendships",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "TransitionActorId",
                table: "friendships",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dismissed_friend_suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dismissed_friend_suggestions", x => x.Id);
                    table.CheckConstraint("ck_no_self_dismissal", "\"UserId\" != \"SuggestedUserId\"");
                    table.ForeignKey(
                        name: "FK_dismissed_friend_suggestions_users_SuggestedUserId",
                        column: x => x.SuggestedUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_dismissed_friend_suggestions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventVersion = table.Column<int>(type: "integer", nullable: false),
                    AggregateDomainVersion = table.Column<long>(type: "bigint", nullable: false),
                    RequestCycleId = table.Column<int>(type: "integer", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    HandlerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Lease = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    Processed = table.Column<bool>(type: "boolean", nullable: false),
                    DeadLettered = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outbox_deliveries_outbox_messages_EventId",
                        column: x => x.EventId,
                        principalTable: "outbox_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_friendships_addressee_status_sentat_id",
                table: "friendships",
                columns: new[] { "AddresseeId", "Status", "SentAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_friendships_requester_status_sentat_id",
                table: "friendships",
                columns: new[] { "RequesterId", "Status", "SentAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_dismissed_friend_suggestions_SuggestedUserId",
                table: "dismissed_friend_suggestions",
                column: "SuggestedUserId");

            migrationBuilder.CreateIndex(
                name: "ix_dismissed_suggestions_expiresat",
                table: "dismissed_friend_suggestions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ix_dismissed_suggestions_user_suggested",
                table: "dismissed_friend_suggestions",
                columns: new[] { "UserId", "SuggestedUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_event_handler",
                table: "outbox_deliveries",
                columns: new[] { "EventId", "HandlerName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_handler_processed_dead",
                table: "outbox_deliveries",
                columns: new[] { "HandlerName", "Processed", "DeadLettered" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_aggregate_event_version",
                table: "outbox_messages",
                columns: new[] { "AggregateId", "EventType", "AggregateDomainVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_occurredat",
                table: "outbox_messages",
                column: "OccurredAtUtc");

            // Forward-only reconciliation of pre-hardening friendship rows to the new history model.
            // These rows predate AcceptedAt/EndedAt/TransitionActorId; infer best-effort values from the
            // terminal Status and the row's UpdatedAt. DomainVersion/RequestCycleId keep their column
            // defaults (1) — downstream consumers (M7/M10/M11) bootstrap from current state at their own
            // activation watermark, so a legacy row starting at version 1 is correct. The COALESCE guards
            // make this idempotent and no row is deleted, reset, or truncated.
            migrationBuilder.Sql("""
                UPDATE friendships
                SET "AcceptedAt" = COALESCE("AcceptedAt", "UpdatedAt"),
                    "TransitionActorId" = COALESCE("TransitionActorId", "AddresseeId")
                WHERE "Status" = 'Accepted';

                UPDATE friendships
                SET "EndedAt" = COALESCE("EndedAt", "UpdatedAt"),
                    "TransitionActorId" = COALESCE("TransitionActorId", "AddresseeId")
                WHERE "Status" = 'Declined';

                UPDATE friendships
                SET "EndedAt" = COALESCE("EndedAt", "UpdatedAt"),
                    "TransitionActorId" = COALESCE("TransitionActorId", "RequesterId")
                WHERE "Status" = 'Cancelled';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dismissed_friend_suggestions");

            migrationBuilder.DropTable(
                name: "outbox_deliveries");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_friendships_addressee_status_sentat_id",
                table: "friendships");

            migrationBuilder.DropIndex(
                name: "ix_friendships_requester_status_sentat_id",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "AcceptedAt",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "DomainVersion",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "NextRequestAllowedAt",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "RequestCycleId",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "TransitionActorId",
                table: "friendships");

            migrationBuilder.CreateIndex(
                name: "IX_friendships_AddresseeId_Status",
                table: "friendships",
                columns: new[] { "AddresseeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_friendships_RequesterId_Status",
                table: "friendships",
                columns: new[] { "RequesterId", "Status" });
        }
    }
}
