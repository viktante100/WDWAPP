using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerPortraitAndNamePrivacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FirstNameOnly",
                table: "PlayerProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<double>(
                name: "ImageX",
                table: "PlayerProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 50.0);

            migrationBuilder.AddColumn<double>(
                name: "ImageY",
                table: "PlayerProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 25.0);

            migrationBuilder.AddColumn<double>(
                name: "ImageZoom",
                table: "PlayerProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstNameOnly",
                table: "PlayerProfiles");

            migrationBuilder.DropColumn(
                name: "ImageX",
                table: "PlayerProfiles");

            migrationBuilder.DropColumn(
                name: "ImageY",
                table: "PlayerProfiles");

            migrationBuilder.DropColumn(
                name: "ImageZoom",
                table: "PlayerProfiles");
        }
    }
}
