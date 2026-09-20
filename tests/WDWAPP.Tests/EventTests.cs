using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WDWAPP.Data;
using WDWAPP.Security;
using WDWAPP.Services;
using Xunit;

namespace WDWAPP.Tests;

public sealed class EventTests : IDisposable
{
    private readonly AuthenticationFactory factory = new();
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9uoAAAAASUVORK5CYII=");
    private static Dictionary<string, string> Fields(bool publish = false) => new()
    {
        ["Input.Type"] = "Tournament", ["Input.Title"] = "Tournament", ["Input.Description"] = "Bring your army!\n<script>alert(1)</script>",
        ["Input.Date"] = "2028-02-29", ["Input.PublishAsNews"] = publish.ToString().ToLowerInvariant()
    };

    [Theory]
    [InlineData(AccessLevels.KeyMember)]
    [InlineData(AccessLevels.Admin)]
    public async Task Editors_can_manage_events_and_linked_news_without_affecting_standalone_news(string role)
    {
        using var client = await Login(role);
        using var visitor = NewClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NewsArticles.Add(new NewsArticle { Title = "Standalone", Body = "Unrelated", PublishedUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }
        var hub = await client.GetStringAsync("/hantera");
        Assert.Contains("href=\"/hantera/evenemang\"", hub);
        Assert.Equal(role == AccessLevels.Admin, hub.Contains("href=\"/admin\""));
        var fields = Fields(true);
        var before = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/hantera/evenemang/ny", "event-edit", fields)).StatusCode);
        var item = Assert.Single(await Events());
        Assert.InRange(item.CreatedUtc, before, DateTime.UtcNow);
        Assert.Null(item.Time);
        Assert.Null(item.ExternalLink);
        Assert.Null(item.ImageData);
        var news = (await News()).Single(article => article.EventId == item.Id);
        Assert.Equal("", news.Title); // No duplicate content in the news row.
        Assert.Equal("Tournament", news.DisplayTitle);
        Assert.Contains("Tournament", await visitor.GetStringAsync("/"));
        var details = await visitor.GetStringAsync($"/evenemang/{item.Id}");
        Assert.Contains("2028-02-29", details);
        Assert.DoesNotContain("<script>alert(1)</script>", details);
        Assert.DoesNotContain("/hantera/evenemang/", details);
        Assert.Contains("Tournament", await visitor.GetStringAsync("/kalender?Month=2028-02&Day=2028-02-29"));
        Assert.DoesNotContain("Tournament", await visitor.GetStringAsync("/kalender?Month=2028-02&Day=2028-02-28"));

        fields["Input.Title"] = "Updated tournament";
        fields["Input.Date"] = "2028-03-01";
        fields["Input.Time"] = "18:30";
        fields["Input.ExternalLink"] = "https://example.org/signup";
        fields["Input.ImageDescription"] = "Tournament picture";
        var editPath = $"/hantera/evenemang/{item.Id}";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, editPath, "event-edit", fields, Png)).StatusCode);
        var updated = Assert.Single(await Events());
        Assert.Equal(item.CreatedUtc, updated.CreatedUtc);
        Assert.Equal(new TimeOnly(18, 30), updated.Time);
        Assert.Equal(Png, await visitor.GetByteArrayAsync(updated.DisplayImageUrl));
        var updatedNews = (await News()).Single(article => article.EventId == item.Id);
        Assert.Equal(news.Id, updatedNews.Id);
        Assert.Equal(news.PublishedUtc, updatedNews.PublishedUtc);
        foreach (var path in new[] { "/", "/nyheter" })
        {
            var html = await visitor.GetStringAsync(path);
            Assert.Contains("Updated tournament", html);
            Assert.Contains("2028-03-01", html);
            Assert.Contains("18:30", html);
            Assert.Contains(updated.DisplayImageUrl!, html);
        }
        Assert.Contains("https://example.org/signup", await visitor.GetStringAsync("/nyheter"));
        Assert.DoesNotContain("Updated tournament", await visitor.GetStringAsync("/kalender?Month=2028-02&Day=2028-02-29"));
        Assert.Contains("Updated tournament", await visitor.GetStringAsync("/kalender?Month=2028-03&Day=2028-03-01"));
        foreach (var suffix in new[] { "", "/ta-bort" })
        {
            var redirect = await client.GetAsync($"/hantera/nyheter/{news.Id}{suffix}");
            Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
            Assert.EndsWith(editPath, redirect.Headers.Location!.ToString());
        }

        fields["Input.PublishAsNews"] = "false";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, editPath, "event-edit", fields)).StatusCode);
        Assert.Equal("Standalone", Assert.Single(await News()).Title);
        Assert.Contains("Standalone", await visitor.GetStringAsync("/"));
        Assert.Single(await Events());
        fields["Input.PublishAsNews"] = "true";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, editPath, "event-edit", fields)).StatusCode);
        Assert.Equal(2, (await News()).Count);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, editPath + "/ta-bort", "event-delete", new())).StatusCode);
        Assert.Single(await Events());
        Assert.Equal(HttpStatusCode.Found, (await Post(client, editPath + "/ta-bort", "event-delete", new() { ["Input.Confirmed"] = "true" })).StatusCode);
        Assert.Empty(await Events());
        Assert.Equal("Standalone", Assert.Single(await News()).Title);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/evenemang/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(updated.DisplayImageUrl)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(AccessLevels.Member)]
    public async Task Non_editors_are_denied_at_routes_and_service_boundary(string? role)
    {
        using var client = role is null ? NewClient() : await Login(role);
        foreach (var path in new[] { "/hantera/evenemang", "/hantera/evenemang/ny", "/hantera/evenemang/1", "/hantera/evenemang/1/ta-bort" })
        {
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(path, new FormUrlEncodedContent(Fields()))).StatusCode);
        }
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<EventService>();
        var identity = new ClaimsIdentity(role is null ? null : "forged");
        identity.AddClaim(new Claim(ClaimTypes.Role, AccessLevels.Admin));
        Assert.False((await service.SaveAsync(new(identity), null, ValidInput())).Succeeded);
        Assert.False((await service.DeleteAsync(new(identity), 1, "version", true)).Succeeded);
        Assert.Empty(await Events());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/kalender")).StatusCode);
    }

    [Theory]
    [InlineData("Input.Title", "")]
    [InlineData("Input.Description", " ")]
    [InlineData("Input.Date", "")]
    [InlineData("Input.Date", "2027-02-29")]
    [InlineData("Input.Time", "25:00")]
    [InlineData("Input.ExternalLink", "javascript:alert(1)")]
    public async Task Invalid_fields_do_not_create_events_or_news(string field, string value)
    {
        using var client = await Login(AccessLevels.KeyMember);
        var fields = Fields(true);
        fields[field] = value;
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/hantera/evenemang/ny", "event-edit", fields)).StatusCode);
        Assert.Empty(await Events());
        Assert.Empty(await News());
    }

    [Fact]
    public async Task Upload_validation_removal_and_missing_file_retry_are_enforced()
    {
        using var client = await Login(AccessLevels.KeyMember);
        var fields = Fields();
        fields["Input.ImageDescription"] = "Picture";
        foreach (var invalid in new[] { "<svg onload='alert(1)'/>"u8.ToArray(), new byte[NewsImage.MaxBytes + 1] })
        {
            Assert.Equal(HttpStatusCode.OK, (await Post(client, "/hantera/evenemang/ny", "event-edit", fields, invalid)).StatusCode);
            Assert.Empty(await Events());
        }
        fields["Input.SelectedImageName"] = "image.png";
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/hantera/evenemang/ny", "event-edit", fields)).StatusCode);
        Assert.Empty(await Events());
        fields["Input.Title"] = "";
        var invalidResponse = await Post(client, "/hantera/evenemang/ny", "event-edit", fields, Png);
        Assert.Equal(HttpStatusCode.OK, invalidResponse.StatusCode);
        Assert.Contains("name=\"Input.SelectedImageName\" value=\"image.png\"", await invalidResponse.Content.ReadAsStringAsync());
        fields["Input.Title"] = "Tournament";
        fields.Remove("Input.ImageDescription");
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/hantera/evenemang/ny", "event-edit", fields, Png)).StatusCode);
        var item = Assert.Single(await Events());
        Assert.Equal("", item.ImageDescription);
        fields.Remove("Input.SelectedImageName");
        fields["Input.RemoveImage"] = "true";
        fields["Input.ImageDescription"] = "";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/hantera/evenemang/{item.Id}", "event-edit", fields)).StatusCode);
        Assert.Null(Assert.Single(await Events()).ImageData);
    }

    [Fact]
    public async Task Antiforgery_stale_edits_direct_news_changes_and_revoked_permissions_are_rejected()
    {
        using var client = await Login(AccessLevels.KeyMember);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/hantera/evenemang/ny", new FormUrlEncodedContent(Fields()))).StatusCode);
        await Post(client, "/hantera/evenemang/ny", "event-edit", Fields(true));
        var item = Assert.Single(await Events());
        var news = Assert.Single(await News());
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = Assert.Single(await users.GetUsersInRoleAsync(AccessLevels.KeyMember));
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        var service = scope.ServiceProvider.GetRequiredService<EventService>();
        var input = ValidInput(); input.Version = item.Version; input.PublishAsNews = true;
        Assert.True((await service.SaveAsync(principal, item.Id, input)).Succeeded);
        Assert.False((await service.SaveAsync(principal, item.Id, input)).Succeeded);
        Assert.False((await service.DeleteAsync(principal, item.Id, item.Version, true)).Succeeded);
        var newsService = scope.ServiceProvider.GetRequiredService<NewsService>();
        Assert.False((await newsService.SaveAsync(principal, news.Id, new() { Title = "Duplicate", Body = "No", Version = news.Version })).Succeeded);
        Assert.False((await newsService.DeleteAsync(principal, news.Id, news.Version, true)).Succeeded);
        using (var demotion = factory.Services.CreateScope())
        {
            var manager = demotion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var current = (await manager.FindByIdAsync(user.Id))!;
            Assert.True((await manager.RemoveFromRoleAsync(current, AccessLevels.KeyMember)).Succeeded);
            Assert.True((await manager.UpdateSecurityStampAsync(current)).Succeeded);
        }
        Assert.False((await service.SaveAsync(principal, null, ValidInput())).Succeeded);
        Assert.False((await service.DeleteAsync(principal, item.Id, (await Events()).Single().Version, true)).Succeeded);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/hantera/evenemang")).StatusCode);
    }

    [Theory]
    [InlineData("2028-02", 29)]
    [InlineData("2027-02", 28)]
    [InlineData("2028-12", 31)]
    [InlineData("0001-01", 31)]
    [InlineData("9999-12", 31)]
    public async Task Calendar_handles_month_lengths_and_boundaries(string month, int days)
    {
        using var client = NewClient();
        var html = await client.GetStringAsync($"/kalender?Month={month}");
        Assert.Equal(days, Regex.Matches(html, "class=\"calendar-day ").Count);
        Assert.Contains("calendar-grid", html);
        Assert.Contains("dagens-evenemang", html);
        if (month == "2028-12") Assert.Contains("Month=2029-01", html);
    }

    private static EventInput ValidInput() => new() { Type = CalendarEventType.Tournament, Title = "Event", Description = "Text", Date = new DateOnly(2028, 2, 29) };
    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private async Task<HttpClient> Login(string role)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var name = Guid.NewGuid().ToString("N");
        var user = new ApplicationUser { UserName = name, Email = name + "@example.test" };
        Assert.True((await users.CreateAsync(user, "Event-password42!")).Succeeded);
        if (role.Length > 0) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/login", "login", new() { ["Input.Email"] = user.Email, ["Input.Password"] = "Event-password42!" })).StatusCode);
        return client;
    }
    private async Task<List<CalendarEvent>> Events()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Events.AsNoTracking().ToListAsync();
    }
    private async Task<List<NewsArticle>> News()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().NewsArticles.AsNoTracking().ToListAsync();
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string handler, Dictionary<string, string> fields, byte[]? image = null)
    {
        var page = await client.GetAsync(path);
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.IsSuccessStatusCode, html);
        foreach (var name in new[] { "__RequestVerificationToken", "Input.Version" })
        {
            var match = Regex.Match(html, "name=\"" + Regex.Escape(name) + "\" value=\"([^\"]*)\"");
            if (match.Success) fields[name] = WebUtility.HtmlDecode(match.Groups[1].Value);
        }
        fields["_handler"] = handler;
        if (image is null) return await client.PostAsync(path, new FormUrlEncodedContent(fields));
        Assert.Contains("enctype=\"multipart/form-data\"", html);
        Assert.Contains("name=\"eventImage\"", html);
        using var form = new MultipartFormDataContent();
        foreach (var pair in fields) form.Add(new StringContent(pair.Value), pair.Key);
        form.Add(new ByteArrayContent(image), "eventImage", "image.png");
        return await client.PostAsync(path, form);
    }
    public void Dispose() => factory.Dispose();
}
