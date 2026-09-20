using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeagueSeasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeasonId",
                table: "LeagueSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LeagueSeasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Term = table.Column<int>(type: "INTEGER", nullable: false),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueSeasons", x => x.Id);
                    table.CheckConstraint("CK_LeagueSeason_Term", "Term IN (1, 2) AND Year BETWEEN 1 AND 9999");
                });

            // Assign existing rounds without discarding any result or rating history.
            migrationBuilder.Sql("""
                INSERT INTO LeagueSeasons (Year, Term, IsClosed, ClosedUtc, Version)
                SELECT DISTINCT CAST(substr(Date, 1, 4) AS INTEGER),
                    CASE WHEN CAST(substr(Date, 6, 2) AS INTEGER) <= 6 THEN 1 ELSE 2 END,
                    1, strftime('%Y-%m-%d %H:%M:%f', 'now'), lower(hex(randomblob(16)))
                FROM LeagueSessions;
                INSERT INTO LeagueSeasons (Year, Term, IsClosed, ClosedUtc, Version)
                SELECT CAST(strftime('%Y', 'now') AS INTEGER),
                    CASE WHEN CAST(strftime('%m', 'now') AS INTEGER) <= 6 THEN 1 ELSE 2 END,
                    1, NULL, lower(hex(randomblob(16)))
                WHERE NOT EXISTS (SELECT 1 FROM LeagueSeasons) AND EXISTS (SELECT 1 FROM LeaguePlayers);
                UPDATE LeagueSeasons SET IsClosed = 0, ClosedUtc = NULL
                WHERE Id = (SELECT Id FROM LeagueSeasons ORDER BY Year DESC, Term DESC LIMIT 1);
                UPDATE LeagueSessions SET SeasonId = (
                    SELECT Id FROM LeagueSeasons
                    WHERE Year = CAST(substr(LeagueSessions.Date, 1, 4) AS INTEGER)
                    AND Term = CASE WHEN CAST(substr(LeagueSessions.Date, 6, 2) AS INTEGER) <= 6 THEN 1 ELSE 2 END);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_LeagueSessions_SeasonId",
                table: "LeagueSessions",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_LeagueSeasons_IsClosed",
                table: "LeagueSeasons",
                column: "IsClosed",
                unique: true,
                filter: "IsClosed = 0");

            migrationBuilder.CreateIndex(
                name: "IX_LeagueSeasons_Year_Term",
                table: "LeagueSeasons",
                columns: new[] { "Year", "Term" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LeagueSessions_LeagueSeasons_SeasonId",
                table: "LeagueSessions",
                column: "SeasonId",
                principalTable: "LeagueSeasons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LeagueSessions_LeagueSeasons_SeasonId",
                table: "LeagueSessions");

            migrationBuilder.DropTable(
                name: "LeagueSeasons");

            migrationBuilder.DropIndex(
                name: "IX_LeagueSessions_SeasonId",
                table: "LeagueSessions");

            migrationBuilder.DropColumn(
                name: "SeasonId",
                table: "LeagueSessions");
        }
    }
}
