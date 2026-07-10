using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimPle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendsAndBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some existing development databases ran the retired
            // 20260529154515_AddFriendsSocialGraph migration from an older branch.
            // Move those tables aside before creating the current schema so their
            // data can be imported without resetting the database.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('public.friendships') IS NOT NULL
                       AND EXISTS (
                           SELECT 1
                           FROM information_schema.columns
                           WHERE table_schema = 'public'
                             AND table_name = 'friendships'
                             AND column_name = 'UserId')
                       AND to_regclass('public.legacy_friendships_m03') IS NULL THEN
                        ALTER TABLE friendships RENAME TO legacy_friendships_m03;
                        ALTER INDEX IF EXISTS "PK_friendships" RENAME TO "PK_legacy_friendships_m03";
                        ALTER INDEX IF EXISTS "IX_friendships_FriendUserId" RENAME TO "IX_legacy_friendships_m03_FriendUserId";
                        ALTER INDEX IF EXISTS "IX_friendships_UserId_FriendUserId" RENAME TO "IX_legacy_friendships_m03_UserId_FriendUserId";
                    END IF;

                    IF to_regclass('public.friend_requests') IS NOT NULL
                       AND to_regclass('public.legacy_friend_requests_m03') IS NULL THEN
                        ALTER TABLE friend_requests RENAME TO legacy_friend_requests_m03;
                        ALTER INDEX IF EXISTS "PK_friend_requests" RENAME TO "PK_legacy_friend_requests_m03";
                        ALTER INDEX IF EXISTS "IX_friend_requests_ReceiverUserId_Status" RENAME TO "IX_legacy_friend_requests_m03_ReceiverUserId_Status";
                        ALTER INDEX IF EXISTS "IX_friend_requests_SenderUserId_ReceiverUserId_Status" RENAME TO "IX_legacy_friend_requests_m03_SenderUserId_ReceiverUserId_Status";
                    END IF;

                    IF to_regclass('public.user_blocks') IS NOT NULL
                       AND to_regclass('public.legacy_user_blocks_m03') IS NULL THEN
                        ALTER TABLE user_blocks RENAME TO legacy_user_blocks_m03;
                        ALTER INDEX IF EXISTS "PK_user_blocks" RENAME TO "PK_legacy_user_blocks_m03";
                        ALTER INDEX IF EXISTS "IX_user_blocks_BlockedUserId" RENAME TO "IX_legacy_user_blocks_m03_BlockedUserId";
                        ALTER INDEX IF EXISTS "IX_user_blocks_BlockerUserId_BlockedUserId" RENAME TO "IX_legacy_user_blocks_m03_BlockerUserId_BlockedUserId";
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "blocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockedId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocks", x => x.Id);
                    table.CheckConstraint("ck_no_self_block", "\"BlockerId\" != \"BlockedId\"");
                    table.ForeignKey(
                        name: "FK_blocks_users_BlockedId",
                        column: x => x.BlockedId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_blocks_users_BlockerId",
                        column: x => x.BlockerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddresseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friendships", x => x.Id);
                    table.CheckConstraint("ck_no_self_friendship", "\"RequesterId\" != \"AddresseeId\"");
                    table.ForeignKey(
                        name: "FK_friendships_users_AddresseeId",
                        column: x => x.AddresseeId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_friendships_users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_friend_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FriendRequestPrivacy = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_friend_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_friend_settings_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blocks_BlockedId",
                table: "blocks",
                column: "BlockedId");

            migrationBuilder.CreateIndex(
                name: "IX_blocks_BlockerId",
                table: "blocks",
                column: "BlockerId");

            migrationBuilder.CreateIndex(
                name: "IX_blocks_BlockerId_BlockedId",
                table: "blocks",
                columns: new[] { "BlockerId", "BlockedId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_friendships_AddresseeId_Status",
                table: "friendships",
                columns: new[] { "AddresseeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_friendships_RequesterId_Status",
                table: "friendships",
                columns: new[] { "RequesterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_user_friend_settings_UserId",
                table: "user_friend_settings",
                column: "UserId",
                unique: true);

            // EF cannot generate expression indexes — added manually.
            // Enforces unordered-pair uniqueness: (A,B) and (B,A) are the same friendship edge.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ix_friendships_unordered_pair
                ON friendships (
                    LEAST(""RequesterId""::text, ""AddresseeId""::text),
                    GREATEST(""RequesterId""::text, ""AddresseeId""::text)
                );
            ");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('public.legacy_friendships_m03') IS NOT NULL THEN
                        INSERT INTO friendships
                            ("Id", "RequesterId", "AddresseeId", "Status", "SentAt", "CreatedAt", "UpdatedAt")
                        SELECT DISTINCT ON (
                                   LEAST("UserId"::text, "FriendUserId"::text),
                                   GREATEST("UserId"::text, "FriendUserId"::text))
                               "Id",
                               "UserId",
                               "FriendUserId",
                               'Accepted',
                               "CreatedAt",
                               "CreatedAt",
                               "UpdatedAt"
                        FROM legacy_friendships_m03
                        WHERE "UserId" <> "FriendUserId"
                        ORDER BY
                            LEAST("UserId"::text, "FriendUserId"::text),
                            GREATEST("UserId"::text, "FriendUserId"::text),
                            "UpdatedAt" DESC,
                            "Id"
                        ON CONFLICT DO NOTHING;
                    END IF;

                    IF to_regclass('public.legacy_friend_requests_m03') IS NOT NULL THEN
                        INSERT INTO friendships
                            ("Id", "RequesterId", "AddresseeId", "Status", "SentAt", "CreatedAt", "UpdatedAt")
                        SELECT DISTINCT ON (
                                   LEAST("SenderUserId"::text, "ReceiverUserId"::text),
                                   GREATEST("SenderUserId"::text, "ReceiverUserId"::text))
                               "Id",
                               "SenderUserId",
                               "ReceiverUserId",
                               CASE "Status"
                                   WHEN 'Pending' THEN 'Pending'
                                   WHEN 'Accepted' THEN 'Accepted'
                                   WHEN 'Declined' THEN 'Declined'
                                   WHEN 'Cancelled' THEN 'Cancelled'
                                   ELSE 'Cancelled'
                               END,
                               "CreatedAt",
                               "CreatedAt",
                               "UpdatedAt"
                        FROM legacy_friend_requests_m03
                        WHERE "SenderUserId" <> "ReceiverUserId"
                        ORDER BY
                            LEAST("SenderUserId"::text, "ReceiverUserId"::text),
                            GREATEST("SenderUserId"::text, "ReceiverUserId"::text),
                            "UpdatedAt" DESC,
                            "Id"
                        ON CONFLICT DO NOTHING;
                    END IF;

                    IF to_regclass('public.legacy_user_blocks_m03') IS NOT NULL THEN
                        INSERT INTO blocks
                            ("Id", "BlockerId", "BlockedId", "CreatedAt", "UpdatedAt")
                        SELECT DISTINCT ON ("BlockerUserId", "BlockedUserId")
                               "Id",
                               "BlockerUserId",
                               "BlockedUserId",
                               "CreatedAt",
                               "UpdatedAt"
                        FROM legacy_user_blocks_m03
                        WHERE "BlockerUserId" <> "BlockedUserId"
                        ORDER BY "BlockerUserId", "BlockedUserId", "UpdatedAt" DESC, "Id"
                        ON CONFLICT DO NOTHING;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'users'
                          AND column_name = 'FriendRequestPolicy') THEN
                        INSERT INTO user_friend_settings
                            ("Id", "UserId", "FriendRequestPrivacy", "CreatedAt", "UpdatedAt")
                        SELECT "Id",
                               "Id",
                               CASE "FriendRequestPolicy"
                                   WHEN 'FriendsOfFriends' THEN 'FriendsOfFriends'
                                   WHEN 'Off' THEN 'Off'
                                   ELSE 'Anyone'
                               END,
                               "CreatedAt",
                               "UpdatedAt"
                        FROM users
                        WHERE "FriendRequestPolicy" IN ('FriendsOfFriends', 'Off')
                        ON CONFLICT DO NOTHING;
                    END IF;

                    UPDATE friendships f
                    SET "Status" = 'Cancelled',
                        "UpdatedAt" = GREATEST(f."UpdatedAt", b."UpdatedAt")
                    FROM blocks b
                    WHERE f."Status" IN ('Pending', 'Accepted')
                      AND ((f."RequesterId" = b."BlockerId" AND f."AddresseeId" = b."BlockedId")
                        OR (f."RequesterId" = b."BlockedId" AND f."AddresseeId" = b."BlockerId"));
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocks");

            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_friendships_unordered_pair;");

            migrationBuilder.DropTable(
                name: "friendships");

            migrationBuilder.DropTable(
                name: "user_friend_settings");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('public.legacy_friendships_m03') IS NOT NULL
                       AND to_regclass('public.friendships') IS NULL THEN
                        ALTER TABLE legacy_friendships_m03 RENAME TO friendships;
                        ALTER INDEX IF EXISTS "PK_legacy_friendships_m03" RENAME TO "PK_friendships";
                        ALTER INDEX IF EXISTS "IX_legacy_friendships_m03_FriendUserId" RENAME TO "IX_friendships_FriendUserId";
                        ALTER INDEX IF EXISTS "IX_legacy_friendships_m03_UserId_FriendUserId" RENAME TO "IX_friendships_UserId_FriendUserId";
                    END IF;

                    IF to_regclass('public.legacy_friend_requests_m03') IS NOT NULL
                       AND to_regclass('public.friend_requests') IS NULL THEN
                        ALTER TABLE legacy_friend_requests_m03 RENAME TO friend_requests;
                        ALTER INDEX IF EXISTS "PK_legacy_friend_requests_m03" RENAME TO "PK_friend_requests";
                        ALTER INDEX IF EXISTS "IX_legacy_friend_requests_m03_ReceiverUserId_Status" RENAME TO "IX_friend_requests_ReceiverUserId_Status";
                        ALTER INDEX IF EXISTS "IX_legacy_friend_requests_m03_SenderUserId_ReceiverUserId_Status" RENAME TO "IX_friend_requests_SenderUserId_ReceiverUserId_Status";
                    END IF;

                    IF to_regclass('public.legacy_user_blocks_m03') IS NOT NULL
                       AND to_regclass('public.user_blocks') IS NULL THEN
                        ALTER TABLE legacy_user_blocks_m03 RENAME TO user_blocks;
                        ALTER INDEX IF EXISTS "PK_legacy_user_blocks_m03" RENAME TO "PK_user_blocks";
                        ALTER INDEX IF EXISTS "IX_legacy_user_blocks_m03_BlockedUserId" RENAME TO "IX_user_blocks_BlockedUserId";
                        ALTER INDEX IF EXISTS "IX_legacy_user_blocks_m03_BlockerUserId_BlockedUserId" RENAME TO "IX_user_blocks_BlockerUserId_BlockedUserId";
                    END IF;
                END $$;
                """);
        }
    }
}
