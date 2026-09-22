using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;
using WDWAPP.Security;

namespace WDWAPP.Services;

public sealed class EventInput : IValidatableObject
{
    [Required(ErrorMessage = "Välj evenemangstyp.")] public CalendarEventType? Type { get; set; }
    [Required, StringLength(160)] public string Title { get; set; } = "";
    [Required, StringLength(20000)] public string Description { get; set; } = "";
    [Required(ErrorMessage = "Ange ett datum.")] public DateOnly? Date { get; set; }
    public string? Time { get; set; }
    [StringLength(2048)] public string? ExternalLink { get; set; }
    [StringLength(250)] public string? ImageDescription { get; set; }
    [StringLength(255)] public string? SelectedImageName { get; set; }
    public bool RemoveImage { get; set; }
    public bool PublishAsNews { get; set; }
    public string Version { get; set; } = "";
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Type is not CalendarEventType.Tournament and not CalendarEventType.GameDay)
            yield return new("Manuella evenemang måste vara Turnering eller Speldag.", [nameof(Type)]);
        if (!string.IsNullOrWhiteSpace(Time) && !TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            yield return new("Ange tid som HH:mm.", [nameof(Time)]);
        if (!string.IsNullOrWhiteSpace(ExternalLink) && !NewsInput.IsWebUrl(ExternalLink))
            yield return new("Länken måste börja med https:// eller http://.", [nameof(ExternalLink)]);
    }
}

public sealed record EventResult(bool Succeeded, string Message, int? Id = null);

public sealed class EventService(ApplicationDbContext database, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
{
    public async Task<EventResult> SaveAsync(ClaimsPrincipal actor, int? id, EventInput input, IFormFile? image = null)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, true))
            return new(false, string.Join(" ", errors.Select(error => error.ErrorMessage)));
        if (!input.RemoveImage && !string.IsNullOrEmpty(input.SelectedImageName) && image is not { Length: > 0 })
            return new(false, "Bilden följde inte med. Välj bildfilen igen innan du sparar.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        if (!await CanManage(actor)) return new(false, "Du saknar behörighet att hantera evenemang. Logga in igen.");
        var item = id.HasValue ? await database.Events.Include(item => item.NewsArticle).SingleOrDefaultAsync(item => item.Id == id)
            : new CalendarEvent { CreatedUtc = DateTime.UtcNow };
        if (item is null) return new(false, "Evenemanget finns inte längre.");
        if (item.LeagueSessionId.HasValue) return new(false, "Ligaevenemang hanteras via 40K-ligan.");
        if (item.MatchRequestId.HasValue) return new(false, "Matchförfrågningar hanteras via Boka match.");
        if (id.HasValue && item.Version != input.Version) return new(false, "Evenemanget har ändrats. Ladda om sidan innan du försöker igen.");
        if (image is { Length: > 0 })
        {
            if (image.Length > NewsImage.MaxBytes) return new(false, "Bilden får vara högst 5 MB.");
            var data = await NewsImage.ReadAsync(image);
            var contentType = NewsImage.ContentType(data);
            if (contentType is null) return new(false, "Välj JPEG, PNG, GIF eller WebP (högst 5 MB).");
            item.ImageData = data;
            item.ImageContentType = contentType;
        }
        else if (input.RemoveImage) { item.ImageData = null; item.ImageContentType = null; }
        item.Type = input.Type!.Value;
        item.Title = input.Title.Trim();
        item.Description = input.Description.Trim();
        item.Date = input.Date!.Value;
        item.Time = string.IsNullOrWhiteSpace(input.Time) ? null : TimeOnly.ParseExact(input.Time, "HH:mm", CultureInfo.InvariantCulture);
        item.ExternalLink = string.IsNullOrWhiteSpace(input.ExternalLink) ? null : input.ExternalLink.Trim();
        // Preserve existing descriptions; new images supplement the event's visible text.
        if (image is { Length: > 0 } || input.RemoveImage) item.ImageDescription = "";
        item.Version = Guid.NewGuid().ToString("N");
        if (!id.HasValue) database.Events.Add(item);
        // News stores publication metadata only. Event content has a single source of truth.
        if (input.PublishAsNews && item.NewsArticle is null)
            item.NewsArticle = new NewsArticle { Event = item, PublishedUtc = DateTime.UtcNow };
        else if (!input.PublishAsNews && item.NewsArticle is not null)
        {
            database.NewsArticles.Remove(item.NewsArticle);
            item.NewsArticle = null;
        }
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(true, "Evenemanget har sparats.", item.Id);
    }

    public async Task<EventResult> DeleteAsync(ClaimsPrincipal actor, int id, string version, bool confirmed)
    {
        if (!confirmed) return new(false, "Bekräfta att evenemanget ska tas bort.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        if (!await CanManage(actor)) return new(false, "Du saknar behörighet att hantera evenemang. Logga in igen.");
        var item = await database.Events.FindAsync(id);
        if (item is null) return new(false, "Evenemanget finns inte längre.");
        if (item.LeagueSessionId.HasValue) return new(false, "Ligaevenemang hanteras via 40K-ligan.");
        if (item.MatchRequestId.HasValue) return new(false, "Matchförfrågningar hanteras via Boka match.");
        if (item.Version != version) return new(false, "Evenemanget har ändrats. Ladda om sidan innan du försöker igen.");
        database.Events.Remove(item);
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(true, "Evenemanget har tagits bort.");
    }

    private async Task<bool> CanManage(ClaimsPrincipal actor)
    {
        database.ChangeTracker.Clear();
        var user = actor.Identity?.IsAuthenticated == true ? await signIn.ValidateSecurityStampAsync(actor) : null;
        return user is not null && (await users.IsInRoleAsync(user, AccessLevels.KeyMember) || await users.IsInRoleAsync(user, AccessLevels.Admin));
    }
}
