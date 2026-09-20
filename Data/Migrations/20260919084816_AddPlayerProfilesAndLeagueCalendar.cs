using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerProfilesAndLeagueCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LeagueSessionId",
                table: "Events",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlayerProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Nickname = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VeizlaIdentity = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Avatar = table.Column<string>(type: "TEXT", nullable: false),
                    ImageData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ImageContentType = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    SelfReportedTitles_LeagueWins = table.Column<int>(type: "INTEGER", nullable: false),
                    SelfReportedTitles_RttWins = table.Column<int>(type: "INTEGER", nullable: false),
                    SelfReportedTitles_ChampionshipWins = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_LeagueSessionId",
                table: "Events",
                column: "LeagueSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerProfiles_UserId",
                table: "PlayerProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Events_LeagueSessions_LeagueSessionId",
                table: "Events",
                column: "LeagueSessionId",
                principalTable: "LeagueSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_LeagueSessions_LeagueSessionId",
                table: "Events");

            migrationBuilder.DropTable(
                name: "PlayerProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Events_LeagueSessionId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "LeagueSessionId",
                table: "Events");
        }
    }
}
