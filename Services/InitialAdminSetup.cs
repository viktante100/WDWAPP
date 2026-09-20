using System.Data;
using Microsoft.AspNetCore.Identity;
using WDWAPP.Data;
using WDWAPP.Security;
using Microsoft.EntityFrameworkCore;

namespace WDWAPP.Services;

// Called only by the explicit local CLI command; never exposed as an HTTP endpoint.
public sealed class InitialAdminSetup(ApplicationDbContext database, UserManager<ApplicationUser> users)
{
    public async Task<AdminResult> PromoteAsync(string email)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        database.ChangeTracker.Clear();
        if ((await users.GetUsersInRoleAsync(AccessLevels.Admin)).Count > 0)
            return new(false, "An admin already exists. Use the Admin page to manage access.");
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
            return new(false, "No account with that email exists. Register the account first.");
        var existingRoles = await users.GetRolesAsync(user);
        if (existingRoles.Count > 0)
        {
            var removed = await users.RemoveFromRolesAsync(user, existingRoles);
            if (!removed.Succeeded) return new(false, "Could not replace the existing role.");
        }
        var result = await users.AddToRoleAsync(user, AccessLevels.Admin);
        if (!result.Succeeded) return new(false, "Could not assign the admin role.");
        result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) return new(false, "Could not invalidate the previous login session.");
        await transaction.CommitAsync();
        return new(true, "Initial admin configured. Sign in again to access the Admin menu.");
    }
}
