using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimPle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGameCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_seed_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestVersion = table.Column<string>(type: "text", nullable: false),
                    Checksum = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_seed_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    RulesSummary = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Difficulty = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EstimatedDurationMinMinutes = table.Column<int>(type: "integer", nullable: false),
                    EstimatedDurationMaxMinutes = table.Column<int>(type: "integer", nullable: false),
                    MinPlayers = table.Column<int>(type: "integer", nullable: false),
                    MaxPlayers = table.Column<int>(type: "integer", nullable: false),
                    Lifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FeaturedRank = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    ArtToken = table.Column<string>(type: "text", nullable: false),
                    ArtColorA = table.Column<string>(type: "text", nullable: false),
                    ArtColorB = table.Column<string>(type: "text", nullable: false),
                    ArtAltText = table.Column<string>(type: "text", nullable: false),
                    ManifestVersion = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_games", x => x.Id);
                    table.CheckConstraint("ck_games_draft_retired_not_featured", "(\"Lifecycle\" <> 'Draft' AND \"Lifecycle\" <> 'Retired') OR \"FeaturedRank\" IS NULL");
                    table.CheckConstraint("ck_games_duration_bounds", "\"EstimatedDurationMinMinutes\" <= \"EstimatedDurationMaxMinutes\"");
                    table.CheckConstraint("ck_games_min_players", "\"MinPlayers\" >= 1 AND \"MinPlayers\" <= \"MaxPlayers\"");
                });

            migrationBuilder.CreateTable(
                name: "game_mode_capabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_mode_capabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_game_mode_capabilities_games_GameId",
                        column: x => x.GameId,
                        principalTable: "games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "game_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_game_tags_games_GameId",
                        column: x => x.GameId,
                        principalTable: "games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_favorite_games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CycleId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_favorite_games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_favorite_games_games_GameId",
                        column: x => x.GameId,
                        principalTable: "games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_favorite_games_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_seed_history_ManifestVersion",
                table: "catalog_seed_history",
                column: "ManifestVersion",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_mode_capabilities_GameId_Mode",
                table: "game_mode_capabilities",
                columns: new[] { "GameId", "Mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_tags_GameId_Value",
                table: "game_tags",
                columns: new[] { "GameId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_games_default_order",
                table: "games",
                columns: new[] { "FeaturedRank", "SortOrder", "Slug" });

            migrationBuilder.CreateIndex(
                name: "ix_games_difficulty_slug",
                table: "games",
                columns: new[] { "Difficulty", "Slug" });

            migrationBuilder.CreateIndex(
                name: "ix_games_duration_slug",
                table: "games",
                columns: new[] { "EstimatedDurationMinMinutes", "Slug" });

            migrationBuilder.CreateIndex(
                name: "ix_games_name_slug",
                table: "games",
                columns: new[] { "Name", "Slug" });

            migrationBuilder.CreateIndex(
                name: "IX_games_Slug",
                table: "games",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_favorite_games_GameId",
                table: "user_favorite_games",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_user_favorite_games_UserId_GameId",
                table: "user_favorite_games",
                columns: new[] { "UserId", "GameId" },
                unique: true);

            // Partial unique index: at most one game may have FeaturedRank = 1. EF cannot express a partial
            // index declaratively, so this is raw SQL — see GameConfiguration.cs.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_games_featured_rank_one ON games (\"FeaturedRank\") WHERE \"FeaturedRank\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_games_featured_rank_one;");

            migrationBuilder.DropTable(
                name: "catalog_seed_history");

            migrationBuilder.DropTable(
                name: "game_mode_capabilities");

            migrationBuilder.DropTable(
                name: "game_tags");

            migrationBuilder.DropTable(
                name: "user_favorite_games");

            migrationBuilder.DropTable(
                name: "games");
        }
    }
}
