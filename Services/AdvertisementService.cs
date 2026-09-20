using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;
using WDWAPP.Security;

namespace WDWAPP.Services;

public sealed record AdvertisementResult(bool Succeeded, string Message, int? Id = null);
public sealed record AdvertisementPreview(int Id, string OwnerUserId, string Title, string Description, AdvertisementType Type,
    DateTime CreatedAt, List<int> ImageIds, List<AdvertisementContactMethod> Contacts);

public sealed class AdvertisementService(ApplicationDbContext database, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn, TimeProvider clock)
{
    public const int MaxImages = 10;
    public const int PageSize = 24;
    // 10 x 5 MB plus multipart headers and form values. Applied only to marketplace POSTs.
    public const long MaxRequestBytes = 55L * 1024 * 1024;
    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
    public IQueryable<Advertisement> Active()
    {
        var now = UtcNow;
        return database.Advertisements.AsNoTracking().Where(a => a.ExpiresAt > now);
    }
    public Task<List<AdvertisementPreview>> ListAsync(int page) => Active().OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
        .Skip((Math.Clamp(page, 1, 100000) - 1) * PageSize).Take(PageSize + 1)
        .Select(a => new AdvertisementPreview(a.Id, a.OwnerUserId, a.Title, a.Description, a.Type, a.CreatedAt,
            a.Images.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).Select(i => i.Id).ToList(),
            a.Contacts.OrderBy(c => c.Type).Select(c => new AdvertisementContactMethod { Type = c.Type, Value = c.Value }).ToList())).ToListAsync();
    public Task<Advertisement?> PublicAsync(int id) => Active().Include(a => a.Contacts).SingleOrDefaultAsync(a => a.Id == id);
    public Task<List<AdvertisementImage>> ImagesAsync(int id) => database.AdvertisementImages.AsNoTracking()
        .Where(i => i.AdvertisementId == id).OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
        .Select(i => new AdvertisementImage { Id = i.Id, AdvertisementId = i.AdvertisementId, SortOrder = i.SortOrder }).ToListAsync();
    private async Task<ApplicationUser?> ActorAsync(ClaimsPrincipal actor) => actor.Identity?.IsAuthenticated == true
        ? await signIn.ValidateSecurityStampAsync(actor) : null;
    public async Task<bool> CanEditAsync(ClaimsPrincipal actor, Advertisement ad)
    {
        var user = await ActorAsync(actor);
        return user is not null && (user.Id == ad.OwnerUserId || await users.IsInRoleAsync(user, AccessLevels.Admin));
    }
    public async Task<AdvertisementResult> SaveAsync(ClaimsPrincipal actor, int? id, AdvertisementInput input, IReadOnlyList<IFormFile>? uploads = null)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, true))
            return new(false, string.Join(" ", errors.Select(e => e.ErrorMessage)));
        uploads ??= [];
        if (uploads.Count > MaxImages) return new(false, "En annons får ha högst 10 bilder.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.ChangeTracker.Clear();
        var user = await ActorAsync(actor);
        if (user is null) return new(false, "Logga in igen.");
        var now = UtcNow;
        var ad = id.HasValue ? await database.Advertisements.Include(a => a.Contacts).SingleOrDefaultAsync(a => a.Id == id)
            : new Advertisement { OwnerUserId = user.Id, CreatedAt = now, ExpiresAt = now.AddMonths(2) };
        if (ad is null || !await CanEditAsync(actor, ad)) return new(false, "Annonsen saknas eller du saknar behörighet.");
        if (!id.HasValue && !(await users.GetRolesAsync(user)).Any(AccessLevels.Roles.Contains))
            return new(false, "Medlemskap krävs för att sätta in en annons.");
        if (ad.ExpiresAt <= now) return new(false, "Annonsen har gått ut och kan inte publiceras igen genom att redigeras.");
        if (id.HasValue && ad.Version != input.Version) return new(false, "Annonsen har ändrats. Ladda om sidan.");
        var images = id.HasValue ? await ImagesAsync(ad.Id) : [];
        var remove = input.RemoveImageIds.ToHashSet();
        if (remove.Any(imageId => !images.Any(i => i.Id == imageId))) return new(false, "Bilden tillhör inte annonsen.");
        if (images.Count - remove.Count + uploads.Count > MaxImages) return new(false, "En annons får ha högst 10 bilder. Ta bort bilder innan du lägger till fler.");
        var added = new List<AdvertisementImage>();
        var order = images.Count == 0 ? 0 : images.Max(i => i.SortOrder) + 1;
        foreach (var upload in uploads)
        {
            if (upload.Length is <= 0 or > NewsImage.MaxBytes) return new(false, "Varje bild måste vara mellan 1 byte och 5 MB.");
            var data = await NewsImage.ReadAsync(upload);
            var contentType = NewsImage.ContentType(data);
            if (contentType is null) return new(false, "Välj JPEG, PNG, GIF eller WebP, högst 5 MB per bild.");
            added.Add(new() { Data = data, ContentType = contentType, SortOrder = order++ });
        }
        // Mutate only after all validation succeeds; failures roll back the entire edit.
        if (remove.Count > 0)
            await database.AdvertisementImages.Where(i => i.AdvertisementId == ad.Id && remove.Contains(i.Id)).ExecuteDeleteAsync();
        ad.Title = input.Title.Trim();
        ad.Description = input.Description.Trim();
        ad.Type = input.Type;
        ad.UpdatedAt = now;
        ad.Version = Guid.NewGuid().ToString("N");
        var contacts = input.Contacts();
        foreach (var old in ad.Contacts.ToList())
        {
            var replacement = contacts.SingleOrDefault(c => c.Type == old.Type);
            if (replacement is null) { database.AdvertisementContacts.Remove(old); ad.Contacts.Remove(old); }
            else { old.Value = replacement.Value; contacts.Remove(replacement); }
        }
        ad.Contacts.AddRange(contacts);
        ad.Images.AddRange(added);
        if (!id.HasValue) database.Advertisements.Add(ad);
        try { await database.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); return new(false, "Annonsen har ändrats. Ladda om sidan."); }
        await transaction.CommitAsync();
        return new(true, "Annonsen har sparats.", ad.Id);
    }
    public async Task<AdvertisementResult> DeleteAsync(ClaimsPrincipal actor, int id, string version, bool confirmed)
    {
        if (!confirmed) return new(false, "Bekräfta att annonsen ska tas bort.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.ChangeTracker.Clear();
        var ad = await database.Advertisements.SingleOrDefaultAsync(a => a.Id == id);
        if (ad is null || !await CanEditAsync(actor, ad)) return new(false, "Annonsen saknas eller du saknar behörighet.");
        if (ad.Version != version) return new(false, "Annonsen har ändrats. Ladda om sidan.");
        database.Advertisements.Remove(ad);
        try { await database.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); return new(false, "Annonsen har ändrats. Ladda om sidan."); }
        await transaction.CommitAsync();
        return new(true, "Annonsen har tagits bort.");
    }
}
