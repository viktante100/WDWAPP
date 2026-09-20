using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeague : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeagueAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeaguePlayers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaguePlayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaguePlayers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeagueSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeagueMatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerOneId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerTwoId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReporterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueMatches", x => x.Id);
                    table.UniqueConstraint("AK_LeagueMatches_Id_SessionId", x => new { x.Id, x.SessionId });
                    table.CheckConstraint("CK_LeagueMatch_Players", "PlayerOneId <> PlayerTwoId AND ReporterId IN (PlayerOneId, PlayerTwoId)");
                    table.CheckConstraint("CK_LeagueMatch_Result", "Outcome BETWEEN 0 AND 2 AND Status BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_LeagueMatches_LeaguePlayers_PlayerOneId",
                        column: x => x.PlayerOneId,
                        principalTable: "LeaguePlayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeagueMatches_LeaguePlayers_PlayerTwoId",
                        column: x => x.PlayerTwoId,
                        principalTable: "LeaguePlayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeagueMatches_LeagueSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "LeagueSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeagueParticipations",
                columns: table => new
                {
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsBye = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueParticipations", x => new { x.SessionId, x.PlayerId });
                    table.CheckConstraint("CK_LeagueParticipation_Bye", "NOT (IsBye = 1 AND MatchId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_LeagueParticipations_LeagueMatches_MatchId_SessionId",
                        columns: x => new { x.MatchId, x.SessionId },
                        principalTable: "LeagueMatches",
                        principalColumns: new[] { "Id", "SessionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeagueParticipations_LeaguePlayers_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "LeaguePlayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeagueParticipations_LeagueSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "LeagueSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeagueRatings",
                columns: table => new
                {
                    MatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Before = table.Column<int>(type: "INTEGER", nullable: false),
                    Change = table.Column<int>(type: "INTEGER", nullable: false),
                    After = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueRatings", x => new { x.MatchId, x.PlayerId });
                    table.ForeignKey(
                        name: "FK_LeagueRatings_LeagueMatches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "LeagueMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeagueRatings_LeaguePlayers_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "LeaguePlayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeagueMatches_PlayerOneId",
                table: "LeagueMatches",
                column: "PlayerOneId");

            migrationBuilder.CreateIndex(
                name: "IX_LeagueMatches_PlayerTwoId",
                table: "LeagueMatches",
                column: "PlayerTwoId");

            migrationBuilder.CreateIndex(
                name: "IX_LeagueMatches_SessionId",
                table: "LeagueMatches",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LeagueParticipations_MatchId_SessionId",
                table: "LeagueParticipations",
                columns: new[] { "MatchId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeagueParticipations_PlayerId",
                table: "LeagueParticipations",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaguePlayers_UserId",
                table: "LeaguePlayers",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeagueRatings_PlayerId",
                table: "LeagueRatings",
                column: "PlayerId");

            // Cross-column overlap cannot be expressed by a normal unique index.
            // These guards complement the unique participation key, including direct SQL writes.
            foreach (var operation in new[] { "INSERT", "UPDATE" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER LeagueMatch_NoOverlap_{operation}
                    BEFORE {operation} ON LeagueMatches
                    BEGIN
                        SELECT RAISE(ABORT, 'A player already has a match or bye in this round')
                        WHERE EXISTS (SELECT 1 FROM LeagueMatches m WHERE m.SessionId = NEW.SessionId
                            AND m.Id <> NEW.Id AND (m.PlayerOneId IN (NEW.PlayerOneId, NEW.PlayerTwoId)
                                OR m.PlayerTwoId IN (NEW.PlayerOneId, NEW.PlayerTwoId)))
                        OR EXISTS (SELECT 1 FROM LeagueParticipations p WHERE p.SessionId = NEW.SessionId
                            AND p.PlayerId IN (NEW.PlayerOneId, NEW.PlayerTwoId) AND p.IsBye = 1);
                    END;
                    """);
                migrationBuilder.Sql($"""
                    CREATE TRIGGER LeagueParticipation_ValidMatch_{operation}
                    BEFORE {operation} ON LeagueParticipations
                    BEGIN
                        SELECT RAISE(ABORT, 'Participation does not match the result')
                        WHERE (NEW.MatchId IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM LeagueMatches m WHERE m.Id = NEW.MatchId AND m.SessionId = NEW.SessionId
                            AND NEW.PlayerId IN (m.PlayerOneId, m.PlayerTwoId)))
                        OR (NEW.IsBye = 1 AND EXISTS (SELECT 1 FROM LeagueMatches m WHERE m.SessionId = NEW.SessionId
                            AND NEW.PlayerId IN (m.PlayerOneId, m.PlayerTwoId)));
                    END;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeagueAudits");

            migrationBuilder.DropTable(
                name: "LeagueParticipations");

            migrationBuilder.DropTable(
                name: "LeagueRatings");

            migrationBuilder.DropTable(
                name: "LeagueMatches");

            migrationBuilder.DropTable(
                name: "LeaguePlayers");

            migrationBuilder.DropTable(
                name: "LeagueSessions");
        }
    }
}
