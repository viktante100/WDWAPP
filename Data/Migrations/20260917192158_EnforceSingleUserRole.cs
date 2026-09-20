using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WDWAPP.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleUserRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Normalize legacy memberships before enforcing one role per user.
            // Invalidate sessions and stale edit forms only for affected accounts.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "SecurityStamp" = lower(hex(randomblob(16))),
                    "ConcurrencyStamp" = lower(hex(randomblob(16)))
                WHERE "Id" IN (
                    SELECT "UserId" FROM "AspNetUserRoles"
                    GROUP BY "UserId" HAVING COUNT(*) > 1
                );

                DELETE FROM "AspNetUserRoles"
                WHERE ("UserId", "RoleId") IN (
                    SELECT "UserId", "RoleId" FROM (
                        SELECT ur."UserId", ur."RoleId",
                            ROW_NUMBER() OVER (
                                PARTITION BY ur."UserId"
                                ORDER BY CASE r."NormalizedName"
                                    WHEN 'ADMIN' THEN 3
                                    WHEN 'KEYMEMBER' THEN 2
                                    WHEN 'MEMBER' THEN 1
                                    ELSE 0 END DESC, ur."RoleId"
                            ) AS position
                        FROM "AspNetUserRoles" ur
                        JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                    ) WHERE position > 1
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_UserId",
                table: "AspNetUserRoles",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUserRoles_UserId",
                table: "AspNetUserRoles");
        }
    }
}
