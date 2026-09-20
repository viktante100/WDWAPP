using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeagueMatchDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlayerOneFaction",
                table: "LeagueMatches",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayerOnePoints",
                table: "LeagueMatches",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlayerTwoFaction",
                table: "LeagueMatches",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayerTwoPoints",
                table: "LeagueMatches",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.DropColumn(
                name: "PlayerOneFaction",
                table: "LeagueMatches");

            migrationBuilder.DropColumn(
                name: "PlayerOnePoints",
                table: "LeagueMatches");

            migrationBuilder.DropColumn(
                name: "PlayerTwoFaction",
                table: "LeagueMatches");

            migrationBuilder.DropColumn(
                name: "PlayerTwoPoints",
                table: "LeagueMatches");
        }
    }
}
