using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvertisements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Advertisements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Advertisements", x => x.Id);
                    table.CheckConstraint("CK_Advertisement_Expiry", "ExpiresAt > CreatedAt");
                    table.CheckConstraint("CK_Advertisement_Type", "Type BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_Advertisements_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdvertisementContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdvertisementId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvertisementContacts", x => x.Id);
                    table.CheckConstraint("CK_AdvertisementContact_Type", "Type BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_AdvertisementContacts_Advertisements_AdvertisementId",
                        column: x => x.AdvertisementId,
                        principalTable: "Advertisements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdvertisementImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdvertisementId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvertisementImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdvertisementImages_Advertisements_AdvertisementId",
                        column: x => x.AdvertisementId,
                        principalTable: "Advertisements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvertisementContacts_AdvertisementId_Type",
                table: "AdvertisementContacts",
                columns: new[] { "AdvertisementId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdvertisementImages_AdvertisementId_SortOrder",
                table: "AdvertisementImages",
                columns: new[] { "AdvertisementId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Advertisements_CreatedAt_Id",
                table: "Advertisements",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Advertisements_ExpiresAt",
                table: "Advertisements",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Advertisements_OwnerUserId",
                table: "Advertisements",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdvertisementContacts");

            migrationBuilder.DropTable(
                name: "AdvertisementImages");

            migrationBuilder.DropTable(
                name: "Advertisements");
        }
    }
}
