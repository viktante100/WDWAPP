using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeagueRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeagueRegistrations",
                columns: table => new
                {
                    SeasonId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    JoinedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueRegistrations", x => new { x.SeasonId, x.PlayerId });
                    table.ForeignKey(
                        name: "FK_LeagueRegistrations_LeaguePlayers_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "LeaguePlayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeagueRegistrations_LeagueSeasons_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "LeagueSeasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeagueRegistrations_PlayerId",
                table: "LeagueRegistrations",
                column: "PlayerId");

            // Preserve proven participation, never copy the global player list into a season.
            migrationBuilder.Sql("""
                INSERT INTO LeagueRegistrations (SeasonId, PlayerId, JoinedUtc)
                SELECT s.SeasonId, p.PlayerId, MIN(MIN(lp.JoinedUtc, s.Date || ' 00:00:00'))
                FROM LeagueParticipations p
                JOIN LeagueSessions s ON s.Id = p.SessionId
                JOIN LeaguePlayers lp ON lp.Id = p.PlayerId
                GROUP BY s.SeasonId, p.PlayerId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeagueRegistrations");
        }
    }
}
