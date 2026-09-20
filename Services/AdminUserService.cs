using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;
using WDWAPP.Security;

namespace WDWAPP.Services;

public sealed record AdminResult(bool Succeeded, string Message);

public sealed class AdminUserService(
    ApplicationDbContext database,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    ILogger<AdminUserService> logger)
{
    public async Task<AdminResult> UpdateDetailsAsync(ClaimsPrincipal actor, string userId, AdminUserInput input)
    {
        if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(input,
            new System.ComponentModel.DataAnnotations.ValidationContext(input), null, true)
            || input.Nickname.Trim().Length < 2)
            return new(false, "Kontrollera användaruppgifterna och försök igen.");

        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        database.ChangeTracker.Clear();
        var administrator = actor.Identity?.IsAuthenticated == true
            ? await signIn.ValidateSecurityStampAsync(actor) : null;
        if (administrator is null || !await users.IsInRoleAsync(administrator, AccessLevels.Admin))
            return new(false, "Du har inte längre administratörsbehörighet. Logga in igen.");
        var user = await users.FindByIdAsync(userId);
        if (user is null) return new(false, "Kontot finns inte längre.");
        if (user.ConcurrencyStamp != input.Version)
            return new(false, "Kontot har ändrats. Ladda om sidan innan du försöker igen.");

        var email = input.Email.Trim();
        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)) user.EmailConfirmed = false;
        if (user.PhoneNumber != Clean(input.PhoneNumber)) user.PhoneNumberConfirmed = false;
        user.UserName = input.Nickname.Trim();
        user.Email = email;
        user.FullName = Clean(input.FullName);
        user.Address = Clean(input.Address);
        var postalCode = Clean(input.PostalCode)?.Replace(" ", "");
        user.PostalCode = postalCode?.Insert(3, " ");
        user.City = Clean(input.City);
        user.PhoneNumber = Clean(input.PhoneNumber);
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) return new(false, string.Join(" ", result.Errors.Select(e => e.Description)));
        var profiles = await database.PlayerProfiles.Where(p => p.UserId == userId).ToListAsync();
        foreach (var profile in profiles)
        {
            profile.DisplayName = PlayerProfile.PublicName(user.FullName, user.UserName, profile.FirstNameOnly);
            profile.Version = Guid.NewGuid().ToString("N");
        }
        await database.SaveChangesAsync();
        result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) return new(false, "Kontot kunde inte uppdateras. Ladda om sidan och försök igen.");
        await transaction.CommitAsync();
        if (administrator.Id == userId) await signIn.RefreshSignInAsync(user);
        logger.LogInformation("Admin {AdminId} updated details for user {UserId}", administrator.Id, userId);
        return new(true, "Användaruppgifterna har uppdaterats.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public Task<AdminResult> ChangeLevelAsync(ClaimsPrincipal actor, string userId, string version, string level)
        => ChangeAsync(actor, userId, version, level, delete: false);

    public Task<AdminResult> DeleteAsync(ClaimsPrincipal actor, string userId, string version, bool confirmed)
        => confirmed ? ChangeAsync(actor, userId, version, null, delete: true)
            : Task.FromResult(new AdminResult(false, "Bekräfta att kontot ska tas bort."));

    private async Task<AdminResult> ChangeAsync(ClaimsPrincipal actor, string userId, string version, string? level, bool delete)
    {
        if (!delete && level != AccessLevels.LoggedIn && !AccessLevels.Roles.Contains(level))
            return new(false, "Ogiltig behörighet.");

        // SQLite's serializable write transaction also serializes competing admin changes.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        database.ChangeTracker.Clear();
        var administrator = actor.Identity?.IsAuthenticated == true
            ? await signIn.ValidateSecurityStampAsync(actor) : null;
        if (administrator is null || !await users.IsInRoleAsync(administrator, AccessLevels.Admin))
            return new(false, "Du har inte längre administratörsbehörighet. Logga in igen.");
        if (administrator.Id == userId)
            return new(false, "Du kan inte ändra din egen behörighet eller ta bort ditt eget konto.");

        var user = await users.FindByIdAsync(userId);
        if (user is null) return new(false, "Kontot finns inte längre.");
        if (string.IsNullOrEmpty(version) || user.ConcurrencyStamp != version)
            return new(false, "Kontot har ändrats. Ladda om sidan innan du försöker igen.");

        var oldRoles = await users.GetRolesAsync(user);
        if (oldRoles.Contains(AccessLevels.Admin) && (delete || level != AccessLevels.Admin)
            && (await users.GetUsersInRoleAsync(AccessLevels.Admin)).Count <= 1)
            return new(false, "Den sista administratören kan inte tas bort eller få lägre behörighet.");

        IdentityResult result;
        if (delete)
        {
            if (await database.LeaguePlayers.AnyAsync(p => p.UserId == userId))
                return new(false, "Kontot har ligahistorik och kan inte tas bort. Ändra behörigheten istället.");
            result = await users.DeleteAsync(user);
        }
        else
        {
            var desiredRoles = level == AccessLevels.LoggedIn ? Array.Empty<string>() : new[] { level! };
            var removedRoles = oldRoles.Except(desiredRoles).ToArray();
            var addedRoles = desiredRoles.Except(oldRoles).ToArray();
            if (removedRoles.Length == 0 && addedRoles.Length == 0)
                return new(true, "Behörigheten är redan sparad.");
            if (removedRoles.Length > 0)
            {
                result = await users.RemoveFromRolesAsync(user, removedRoles);
                if (!result.Succeeded) return new(false, "Kontot kunde inte uppdateras. Ladda om sidan och försök igen.");
            }
            if (addedRoles.Length > 0)
            {
                result = await users.AddToRolesAsync(user, addedRoles);
                if (!result.Succeeded) return new(false, "Kontot kunde inte uppdateras. Ladda om sidan och försök igen.");
            }
            result = await users.UpdateSecurityStampAsync(user);
        }
        if (!result.Succeeded) return new(false, "Kontot kunde inte uppdateras. Ladda om sidan och försök igen.");

        await transaction.CommitAsync();
        logger.LogInformation("Admin {AdminId} performed {Operation} on user {UserId}. New level: {Level}",
            administrator.Id, delete ? "Delete" : "ChangeLevel", userId, level);
        return new(true, delete ? "Kontot har tagits bort." : "Behörigheten har uppdaterats.");
    }
}
