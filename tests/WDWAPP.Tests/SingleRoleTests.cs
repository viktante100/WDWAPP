using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WDWAPP.Data;
using WDWAPP.Security;
using Xunit;

namespace WDWAPP.Tests;

public sealed class SingleRoleTests
{
    [Fact]
    public async Task Migration_keeps_highest_role_and_database_rejects_second_role()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260917182313_InitialIdentity");

        foreach (var role in AccessLevels.Roles)
            database.Roles.Add(new IdentityRole { Id = role, Name = role, NormalizedName = role.ToUpperInvariant() });
        foreach (var id in new[] { "admin", "keymember", "member", "basic" })
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AspNetUsers" ("Id", "UserName", "SecurityStamp", "ConcurrencyStamp",
                    "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ({id}, {id}, 'original', 'original', 0, 0, 0, 0, 0)
                """);
        foreach (var role in AccessLevels.Roles)
            database.UserRoles.Add(new IdentityUserRole<string> { UserId = "admin", RoleId = role });
        database.UserRoles.Add(new IdentityUserRole<string> { UserId = "keymember", RoleId = AccessLevels.KeyMember });
        database.UserRoles.Add(new IdentityUserRole<string> { UserId = "keymember", RoleId = AccessLevels.Member });
        database.UserRoles.Add(new IdentityUserRole<string> { UserId = "member", RoleId = AccessLevels.Member });
        await database.SaveChangesAsync();

        await migrator.MigrateAsync();
        database.ChangeTracker.Clear();
        var roles = await database.UserRoles.ToListAsync();
        Assert.Equal(3, roles.Count);
        Assert.Equal(AccessLevels.Admin, roles.Single(role => role.UserId == "admin").RoleId);
        Assert.Equal(AccessLevels.KeyMember, roles.Single(role => role.UserId == "keymember").RoleId);
        Assert.Equal(AccessLevels.Member, roles.Single(role => role.UserId == "member").RoleId);
        Assert.DoesNotContain(roles, role => role.UserId == "basic");
        Assert.NotEqual("original", (await database.Users.FindAsync("admin"))!.SecurityStamp);
        Assert.NotEqual("original", (await database.Users.FindAsync("keymember"))!.ConcurrencyStamp);
        Assert.Equal("original", (await database.Users.FindAsync("member"))!.SecurityStamp);
        Assert.Equal("original", (await database.Users.FindAsync("basic"))!.SecurityStamp);
        Assert.Null((await database.Users.FindAsync("basic"))!.FullName);
        Assert.Null((await database.Users.FindAsync("basic"))!.Address);

        database.UserRoles.Add(new IdentityUserRole<string> { UserId = "member", RoleId = AccessLevels.Admin });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
    }
}
