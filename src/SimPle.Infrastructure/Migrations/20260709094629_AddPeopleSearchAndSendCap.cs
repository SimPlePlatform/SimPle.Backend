using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimPle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPeopleSearchAndSendCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastSenderId",
                table: "friendships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SendCountInWindow",
                table: "friendships",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SendWindowStartUtc",
                table: "friendships",
                type: "timestamp with time zone",
                nullable: true);

            // EF's fluent API cannot express operator-class-qualified indexes. Both accelerate people-search
            // prefix scans (LIKE 'X%' / normalized-prefix lookups) under a non-C default collation, where a
            // plain btree index degrades to a sequential scan for pattern matching.
            migrationBuilder.Sql(@"
                CREATE INDEX ix_users_normalizedusername_pattern
                ON users (""NormalizedUsername"" varchar_pattern_ops);
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX ix_users_displayname_upper_pattern
                ON users (upper(""DisplayName"") text_pattern_ops);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_users_displayname_upper_pattern;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_users_normalizedusername_pattern;");

            migrationBuilder.DropColumn(
                name: "LastSenderId",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "SendCountInWindow",
                table: "friendships");

            migrationBuilder.DropColumn(
                name: "SendWindowStartUtc",
                table: "friendships");
        }
    }
}
