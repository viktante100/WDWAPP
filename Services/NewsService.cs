using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;
using WDWAPP.Security;

namespace WDWAPP.Services;

public sealed class NewsInput : IValidatableObject
{
    [Required, StringLength(160)] public string Title { get; set; } = "";
    [Required, StringLength(20000)] public string Body { get; set; } = "";
    [StringLength(2048)] public string? ImageUrl { get; set; }
    [StringLength(250)] public string? ImageDescription { get; set; }
    [StringLength(10000)] public string? Links { get; set; }
    public string Version { get; set; } = "";
    public bool RemoveImage { get; set; }
    [StringLength(255)] public string? SelectedImageName { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(ImageUrl) && !IsWebUrl(ImageUrl))
            yield return new("Bildadressen måste börja med https:// eller http://.", [nameof(ImageUrl)]);
        if (!RemoveImage && !string.IsNullOrWhiteSpace(ImageUrl) && string.IsNullOrWhiteSpace(ImageDescription))
            yield return new("Beskriv bilden för besökare som inte kan se den.", [nameof(ImageDescription)]);
        if (LinkLines(Links).Any(link => link.Length > 2048 || !IsWebUrl(link)))
            yield return new("Ange en fullständig http://- eller https://-länk per rad.", [nameof(Links)]);
    }

    public static bool IsWebUrl(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(uri.Host);
    public static string[] LinkLines(string? value) => (value ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed record NewsResult(bool Succeeded, string Message);

public sealed class NewsService(ApplicationDbContext database, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn)
{
    public async Task<NewsResult> SaveAsync(ClaimsPrincipal actor, int? id, NewsInput input, IFormFile? image = null)
    {
        if (!input.RemoveImage && !string.IsNullOrEmpty(input.SelectedImageName) && image is not { Length: > 0 })
            return new(false, "Bilden följde inte med. Välj bildfilen igen innan du sparar.");
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), errors, true))
            return new(false, string.Join(" ", errors.Select(error => error.ErrorMessage)));
        await using var transaction = await database.Database.BeginTransactionAsync();
        if (!await CanManage(actor)) return new(false, "Du saknar behörighet att hantera nyheter. Logga in igen.");
        var article = id.HasValue ? await database.NewsArticles.FindAsync(id.Value) : new NewsArticle { PublishedUtc = DateTime.UtcNow };
        if (article is null) return new(false, "Nyheten finns inte längre.");
        if (article.EventId.HasValue) return new(false, "Hantera denna nyhet via dess evenemang.");
        if (id.HasValue && article.Version != input.Version)
            return new(false, "Nyheten har ändrats. Ladda om sidan innan du försöker igen.");
        if (image is { Length: > 0 })
        {
            if (image.Length > NewsImage.MaxBytes)
                return new(false, "Bilden får vara högst 5 MB.");
            var data = await NewsImage.ReadAsync(image);
            var contentType = NewsImage.ContentType(data);
            if (contentType is null)
                return new(false, "Välj en bild i JPEG-, PNG-, GIF- eller WebP-format (högst 5 MB).");
            article.ImageData = data;
            article.ImageContentType = contentType;
        }
        else if (input.RemoveImage)
        {
            article.ImageData = null;
            article.ImageContentType = null;
        }
        if (article.ImageData is not null && string.IsNullOrWhiteSpace(input.ImageDescription))
            return new(false, "Beskriv bilden för besökare som inte kan se den.");
        article.Title = input.Title.Trim();
        article.Body = input.Body.Trim();
        article.ImageUrl = input.RemoveImage || article.ImageData is not null || string.IsNullOrWhiteSpace(input.ImageUrl)
            ? null : input.ImageUrl.Trim();
        article.ImageDescription = input.ImageDescription?.Trim();
        article.Links = string.Join('\n', NewsInput.LinkLines(input.Links));
        article.Version = Guid.NewGuid().ToString("N");
        if (!id.HasValue) database.NewsArticles.Add(article);
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(true, "Nyheten har sparats.");
    }

    public async Task<NewsResult> DeleteAsync(ClaimsPrincipal actor, int id, string version, bool confirmed)
    {
        if (!confirmed) return new(false, "Bekräfta att nyheten ska tas bort.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        if (!await CanManage(actor)) return new(false, "Du saknar behörighet att hantera nyheter. Logga in igen.");
        var article = await database.NewsArticles.FindAsync(id);
        if (article is null) return new(false, "Nyheten finns inte längre.");
        if (article.EventId.HasValue) return new(false, "Avpublicera denna nyhet via dess evenemang.");
        if (article.Version != version) return new(false, "Nyheten har ändrats. Ladda om sidan innan du försöker igen.");
        database.NewsArticles.Remove(article);
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(true, "Nyheten har tagits bort.");
    }

    private async Task<bool> CanManage(ClaimsPrincipal actor)
    {
        database.ChangeTracker.Clear();
        var user = actor.Identity?.IsAuthenticated == true ? await signIn.ValidateSecurityStampAsync(actor) : null;
        return user is not null && (await users.IsInRoleAsync(user, AccessLevels.Admin) || await users.IsInRoleAsync(user, AccessLevels.KeyMember));
    }
}
