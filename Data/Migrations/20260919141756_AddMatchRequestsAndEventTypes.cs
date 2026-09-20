using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchRequestsAndEventTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MatchRequestId",
                table: "Events",
                type: "INTEGER",
                nullable: true);

            // Add and backfill before SQLite rebuilds Events to apply the new constraint.
            // Existing manual events become GameDay; titles are never used to guess categories.
            migrationBuilder.Sql("ALTER TABLE Events ADD COLUMN Type INTEGER NOT NULL DEFAULT 0;");
            migrationBuilder.Sql("UPDATE Events SET Type = 2 WHERE LeagueSessionId IS NOT NULL;");

            migrationBuilder.CreateTable(
                name: "MatchRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerPlayerProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    AcceptedByPlayerProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Game = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchRequests", x => x.Id);
                    table.CheckConstraint("CK_MatchRequest_Acceptance", "(AcceptedByPlayerProfileId IS NULL AND AcceptedUtc IS NULL) OR (AcceptedByPlayerProfileId IS NOT NULL AND AcceptedUtc IS NOT NULL AND AcceptedByPlayerProfileId <> OwnerPlayerProfileId)");
                    table.ForeignKey(
                        name: "FK_MatchRequests_PlayerProfiles_AcceptedByPlayerProfileId",
                        column: x => x.AcceptedByPlayerProfileId,
                        principalTable: "PlayerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MatchRequests_PlayerProfiles_OwnerPlayerProfileId",
                        column: x => x.OwnerPlayerProfileId,
                        principalTable: "PlayerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_MatchRequestId",
                table: "Events",
                column: "MatchRequestId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Event_Type",
                table: "Events",
                sql: "(Type IN (0, 1) AND LeagueSessionId IS NULL AND MatchRequestId IS NULL) OR (Type = 2 AND LeagueSessionId IS NOT NULL AND MatchRequestId IS NULL) OR (Type = 3 AND MatchRequestId IS NOT NULL AND LeagueSessionId IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_MatchRequests_AcceptedByPlayerProfileId",
                table: "MatchRequests",
                column: "AcceptedByPlayerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchRequests_OwnerPlayerProfileId",
                table: "MatchRequests",
                column: "OwnerPlayerProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_MatchRequests_MatchRequestId",
                table: "Events",
                column: "MatchRequestId",
                principalTable: "MatchRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_MatchRequests_MatchRequestId",
                table: "Events");

            migrationBuilder.DropTable(
                name: "MatchRequests");

            migrationBuilder.DropIndex(
                name: "IX_Events_MatchRequestId",
                table: "Events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Event_Type",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "MatchRequestId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Events");
        }
    }
}
