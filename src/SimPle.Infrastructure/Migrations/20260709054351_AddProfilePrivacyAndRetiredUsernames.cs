using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimPle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfilePrivacyAndRetiredUsernames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FriendsListVisibility",
                table: "user_friend_settings",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Friends");

            migrationBuilder.AddColumn<long>(
                name: "PrivacyPolicyVersion",
                table: "user_friend_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "SearchVisibility",
                table: "user_friend_settings",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Everyone");

            // M03-009 fix: the uniform "Everyone" default above only seeds a value for the AddColumn
            // itself; correct every existing row to match its owning user's current ProfileVisibility, per
            // spec-r2 ("search defaults to match current profile eligibility"): Public -> Everyone,
            // FriendsOnly -> FriendsOfFriends, Private -> Nobody. Without this, a pre-migration Private
            // user becomes searchable the moment this migration runs.
            migrationBuilder.Sql(@"
                UPDATE user_friend_settings ufs
                SET ""SearchVisibility"" = CASE u.""Visibility""
                    WHEN 'FriendsOnly' THEN 'FriendsOfFriends'
                    WHEN 'Private' THEN 'Nobody'
                    ELSE 'Everyone'
                END
                FROM users u
                WHERE u.""Id"" = ufs.""UserId"";
            ");

            migrationBuilder.CreateTable(
                name: "retired_usernames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedUsername = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PriorOwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retired_usernames", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_retired_usernames_NormalizedUsername",
                table: "retired_usernames",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retired_usernames_PriorOwnerUserId",
                table: "retired_usernames",
                column: "PriorOwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retired_usernames");

            migrationBuilder.DropColumn(
                name: "FriendsListVisibility",
                table: "user_friend_settings");

            migrationBuilder.DropColumn(
                name: "PrivacyPolicyVersion",
                table: "user_friend_settings");

            migrationBuilder.DropColumn(
                name: "SearchVisibility",
                table: "user_friend_settings");
        }
    }
}
