using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkMatchRequestsToAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill before SQLite rebuilds the table and removes the old profile keys.
            migrationBuilder.Sql("ALTER TABLE MatchRequests ADD COLUMN OwnerUserId TEXT NOT NULL DEFAULT ''; ");
            migrationBuilder.Sql("ALTER TABLE MatchRequests ADD COLUMN AcceptedByUserId TEXT NULL;");
            migrationBuilder.Sql("""
                UPDATE MatchRequests SET
                    OwnerUserId = (SELECT UserId FROM PlayerProfiles WHERE Id = OwnerPlayerProfileId),
                    AcceptedByUserId = (SELECT UserId FROM PlayerProfiles WHERE Id = AcceptedByPlayerProfileId);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_MatchRequests_PlayerProfiles_AcceptedByPlayerProfileId",
                table: "MatchRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_MatchRequests_PlayerProfiles_OwnerPlayerProfileId",
                table: "MatchRequests");

            migrationBuilder.DropIndex(
                name: "IX_MatchRequests_AcceptedByPlayerProfileId",
                table: "MatchRequests");

            migrationBuilder.DropIndex(
                name: "IX_MatchRequests_OwnerPlayerProfileId",
                table: "MatchRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MatchRequest_Acceptance",
                table: "MatchRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedByPlayerProfileId",
                table: "MatchRequests");

            migrationBuilder.DropColumn(
                name: "OwnerPlayerProfileId",
                table: "MatchRequests");

            migrationBuilder.CreateIndex(
                name: "IX_MatchRequests_AcceptedByUserId",
                table: "MatchRequests",
                column: "AcceptedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchRequests_OwnerUserId",
                table: "MatchRequests",
                column: "OwnerUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MatchRequest_Acceptance",
                table: "MatchRequests",
                sql: "(AcceptedByUserId IS NULL AND AcceptedUtc IS NULL) OR (AcceptedByUserId IS NOT NULL AND AcceptedUtc IS NOT NULL AND AcceptedByUserId <> OwnerUserId)");

            migrationBuilder.AddForeignKey(
                name: "FK_MatchRequests_AspNetUsers_AcceptedByUserId",
                table: "MatchRequests",
                column: "AcceptedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MatchRequests_AspNetUsers_OwnerUserId",
                table: "MatchRequests",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Cannot restore profile-only matches: accounts may have no player profile. Restore a pre-migration backup if rollback is required.");
        }
    }
}
