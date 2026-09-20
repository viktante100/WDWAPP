using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;
using WDWAPP.Security;

namespace WDWAPP.Services;

public sealed class PlayerProfileInput
{
    public string DisplayName { get; set; } = ""; // Legacy clients; the server always takes the account name.
    public bool FirstNameOnly { get; set; } = true;
    [Range(1d, 4d)] public double ImageZoom { get; set; } = 1;
    [Range(0d, 100d)] public double ImageX { get; set; } = 50;
    [Range(0d, 100d)] public double ImageY { get; set; } = 25;
    [StringLength(100)] public string? Nickname { get; set; }
    [StringLength(100)] public string? VeizlaIdentity { get; set; }
    [Required] public string Avatar { get; set; } = "person-circle";
    [Range(0, 10000)] public int LeagueWins { get; set; }
    [Range(0, 10000)] public int RttWins { get; set; }
    [Range(0, 10000)] public int ChampionshipWins { get; set; }
    public bool RemoveImage { get; set; }
    public string Version { get; set; } = "";
}
public sealed record PlayerProfileResult(bool Succeeded, string Message, int? Id = null);

public sealed class PlayerProfileService(ApplicationDbContext database, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
{
    public static readonly string[] Avatars = ["person-circle", "person-fill", "question-circle"];
    private async Task<ApplicationUser?> Actor(ClaimsPrincipal actor) => actor.Identity?.IsAuthenticated == true
        ? await signIn.ValidateSecurityStampAsync(actor) : null;
    public async Task<bool> CanEditAsync(ClaimsPrincipal actor, PlayerProfile profile)
    {
        var user = await Actor(actor);
        return user is not null && (profile.UserId == user.Id || await users.IsInRoleAsync(user, AccessLevels.Admin));
    }
    public async Task<PlayerProfileResult> SaveAsync(ClaimsPrincipal actor, int? id, PlayerProfileInput input, IFormFile? image = null)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, true) || !Avatars.Contains(input.Avatar))
            return new(false, "Kontrollera namn, bildval och titlar (0–10 000).");
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.ChangeTracker.Clear();
        var user = await Actor(actor);
        if (user is null) return new(false, "Logga in igen.");
        var profile = id.HasValue ? await database.PlayerProfiles.SingleOrDefaultAsync(p => p.Id == id) : new PlayerProfile { UserId = user.Id };
        if (profile is null || !await CanEditAsync(actor, profile)) return new(false, "Du saknar behörighet till profilen.");
        if (!id.HasValue)
        {
            if (!(await users.GetRolesAsync(user)).Any(AccessLevels.Roles.Contains)) return new(false, "Medlemskap krävs för att skapa en spelarprofil.");
            if (await database.PlayerProfiles.AnyAsync(p => p.UserId == user.Id)) return new(false, "Du har redan en spelarprofil.");
        }
        else if (profile.Version != input.Version) return new(false, "Profilen har ändrats. Ladda om sidan.");
        byte[]? data = null;
        string? contentType = null;
        if (image is not null)
        {
            if (image.Length is <= 0 or > NewsImage.MaxBytes) return new(false, "Välj en bild på högst 5 MB.");
            data = await NewsImage.ReadAsync(image);
            contentType = NewsImage.ContentType(data);
            if (contentType is null) return new(false, "Välj JPEG, PNG, GIF eller WebP på högst 5 MB.");
        }
        var owner = profile.UserId == user.Id ? user : await users.FindByIdAsync(profile.UserId);
        if (owner is null) return new(false, "Spelarens konto saknas.");
        profile.FirstNameOnly = input.FirstNameOnly;
        profile.DisplayName = PlayerProfile.PublicName(owner.FullName, owner.UserName, input.FirstNameOnly);
        profile.ImageZoom = input.ImageZoom;
        profile.ImageX = input.ImageX;
        profile.ImageY = input.ImageY;
        profile.Nickname = input.Nickname?.Trim();
        if (input.VeizlaIdentity is not null) profile.VeizlaIdentity = input.VeizlaIdentity.Trim();
        profile.Avatar = input.Avatar;
        if (data is not null) { profile.ImageData = data; profile.ImageContentType = contentType; }
        else if (input.RemoveImage) { profile.ImageData = null; profile.ImageContentType = null; }
        profile.SelfReportedTitles = new() { LeagueWins = input.LeagueWins, RttWins = input.RttWins, ChampionshipWins = input.ChampionshipWins };
        profile.Version = Guid.NewGuid().ToString("N");
        if (!id.HasValue) database.PlayerProfiles.Add(profile);
        try { await database.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); return new(false, "Profilen har ändrats. Ladda om sidan."); }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 })
        { database.ChangeTracker.Clear(); return new(false, "Du har redan en spelarprofil."); }
        await transaction.CommitAsync();
        return new(true, "Profilen har sparats.", profile.Id);
    }
    public async Task<PlayerProfileResult> DeleteAsync(ClaimsPrincipal actor, int id, string version, bool confirmed)
    {
        if (!confirmed) return new(false, "Bekräfta att profilen ska tas bort.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.ChangeTracker.Clear();
        var profile = await database.PlayerProfiles.SingleOrDefaultAsync(p => p.Id == id);
        if (profile is null || !await CanEditAsync(actor, profile)) return new(false, "Du saknar behörighet till profilen.");
        if (profile.Version != version) return new(false, "Profilen har ändrats. Ladda om sidan.");
        database.PlayerProfiles.Remove(profile);
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(true, "Profilen har tagits bort.");
    }
}
