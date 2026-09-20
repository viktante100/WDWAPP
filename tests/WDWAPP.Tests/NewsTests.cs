using System.Net;
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

public sealed class NewsTests : IDisposable
{
    private readonly AuthenticationFactory factory = new();

    [Theory]
    [InlineData(AccessLevels.KeyMember)]
    [InlineData(AccessLevels.Admin)]
    public async Task Editors_can_publish_edit_and_delete_and_home_shows_latest(string role)
    {
        using var client = await Login(role);
        using var visitor = NewClient();
        Assert.Contains("På gång hos WDW", WebUtility.HtmlDecode(await visitor.GetStringAsync("/")));
        var hub = await client.GetStringAsync("/hantera");
        Assert.Equal(role == AccessLevels.Admin, hub.Contains("href=\"/admin\""));
        var fields = new Dictionary<string, string>
        {
            ["Input.Title"] = "Season opening", ["Input.Body"] = "Welcome!\n<script>alert(1)</script>",
            ["Input.ImageUrl"] = "https://example.org/news.jpg", ["Input.ImageDescription"] = "Players",
            ["Input.Links"] = "https://example.org/signup\nhttps://example.org/rules"
        };
        var before = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/hantera/nyheter/ny", "news-edit", fields)).StatusCode);
        var first = Assert.Single(await Articles());
        Assert.InRange(first.PublishedUtc, before, DateTime.UtcNow);
        fields["Input.Title"] = "Tournament trip";
        fields["Input.Body"] = new string('a', 400) + "END OF ARTICLE";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/hantera/nyheter/ny", "news-edit", fields)).StatusCode);
        var second = (await Articles()).Single(article => article.Id != first.Id);
        var home = await visitor.GetStringAsync("/");
        Assert.Contains("Tournament trip", home);
        Assert.DoesNotContain("Season opening", home);
        Assert.DoesNotContain("END OF ARTICLE", home);
        Assert.Contains($"/nyheter#nyhet-{second.Id}", home);
        var archive = await visitor.GetStringAsync("/nyheter");
        Assert.True(archive.IndexOf("Tournament trip") < archive.IndexOf("Season opening"));
        Assert.Contains("END OF ARTICLE", archive);
        Assert.Contains("https://example.org/news.jpg", archive);
        Assert.Contains("href=\"https://example.org/signup\"", archive);
        Assert.DoesNotContain("<script>alert(1)</script>", archive);
        Assert.Contains("<time datetime=", archive);

        fields["Input.Title"] = "Season edited";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/hantera/nyheter/{first.Id}", "news-edit", fields)).StatusCode);
        Assert.Equal(first.PublishedUtc, (await Articles()).Single(article => article.Id == first.Id).PublishedUtc);
        Assert.Contains("Tournament trip", await visitor.GetStringAsync("/"));
        var path = $"/hantera/nyheter/{second.Id}/ta-bort";
        Assert.Equal(HttpStatusCode.OK, (await Post(client, path, "news-delete", new())).StatusCode);
        Assert.Equal(2, (await Articles()).Count);
        Assert.Equal(HttpStatusCode.Found, (await Post(client, path, "news-delete", new() { ["Input.Confirmed"] = "true" })).StatusCode);
        Assert.Contains("Season edited", await visitor.GetStringAsync("/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AccessLevels.Member)]
    public async Task Visitors_and_members_cannot_manage_news(string? role)
    {
        using var client = role is null ? NewClient() : await Login(role);
        Assert.DoesNotContain("href=\"/hantera\"", await client.GetStringAsync("/"));
        foreach (var path in new[] { "/hantera", "/hantera/nyheter/ny", "/hantera/nyheter/1", "/hantera/nyheter/1/ta-bort" })
        {
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        }
    }

    [Fact]
    public async Task Forged_posts_unsafe_links_stale_edits_and_demoted_editors_are_rejected()
    {
        using var client = await Login(AccessLevels.KeyMember);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/hantera/nyheter/ny", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        var fields = new Dictionary<string, string> { ["Input.Title"] = "News", ["Input.Body"] = "Body", ["Input.Links"] = "javascript:alert(1)" };
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/hantera/nyheter/ny", "news-edit", fields)).StatusCode);
        Assert.Empty(await Articles());
        fields["Input.Links"] = "https://example.org";
        await Post(client, "/hantera/nyheter/ny", "news-edit", fields);
        var article = Assert.Single(await Articles());
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = Assert.Single(await users.GetUsersInRoleAsync(AccessLevels.KeyMember));
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        var service = scope.ServiceProvider.GetRequiredService<NewsService>();
        Assert.True((await service.SaveAsync(principal, article.Id, new() { Title = "Edited", Body = "Body", Version = article.Version })).Succeeded);
        Assert.False((await service.SaveAsync(principal, article.Id, new() { Title = "Stale", Body = "Body", Version = article.Version })).Succeeded);
        Assert.False((await service.DeleteAsync(principal, article.Id, article.Version, true)).Succeeded);
        using (var demotionScope = factory.Services.CreateScope())
        {
            var demotionUsers = demotionScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var currentUser = (await demotionUsers.FindByIdAsync(user.Id))!;
            Assert.True((await demotionUsers.RemoveFromRoleAsync(currentUser, AccessLevels.KeyMember)).Succeeded);
            Assert.True((await demotionUsers.UpdateSecurityStampAsync(currentUser)).Succeeded);
        }
        Assert.False((await service.SaveAsync(principal, null, new() { Title = "Forbidden", Body = "Body" })).Succeeded);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/hantera")).StatusCode);
    }

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private async Task<HttpClient> Login(string role)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var name = Guid.NewGuid().ToString("N");
        var user = new ApplicationUser { UserName = name, Email = name + "@example.test" };
        Assert.True((await users.CreateAsync(user, "News-password42!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/login", "login", new() { ["Input.Email"] = user.Email, ["Input.Password"] = "News-password42!" })).StatusCode);
        return client;
    }
    [Fact]
    public async Task Local_images_upload_replace_remove_and_delete_with_article()
    {
        using var client = await Login(AccessLevels.KeyMember);
        using var visitor = NewClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9uoAAAAASUVORK5CYII=");
        Assert.Equal(HttpStatusCode.Found, (await Upload(client, "/hantera/nyheter/ny", png)).StatusCode);
        var article = Assert.Single(await Articles());
        var imagePath = article.DisplayImageUrl!;
        var response = await visitor.GetAsync(imagePath);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(imagePath.Replace("&", "&amp;"), await visitor.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await Upload(client, $"/hantera/nyheter/{article.Id}", "<svg onload='alert(1)'/>"u8.ToArray())).StatusCode);
        Assert.Equal(png, Assert.Single(await Articles()).ImageData);
        Assert.Equal(HttpStatusCode.OK, (await Upload(client, $"/hantera/nyheter/{article.Id}", new byte[NewsImage.MaxBytes + 1])).StatusCode);
        Assert.Equal(png, Assert.Single(await Articles()).ImageData);
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/hantera/nyheter/{article.Id}", "news-edit", new()
        { ["Input.Title"] = "Text edit", ["Input.Body"] = "Body", ["Input.ImageDescription"] = "Picture" })).StatusCode);
        Assert.Equal(png, Assert.Single(await Articles()).ImageData);
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/hantera/nyheter/{article.Id}", "news-edit", new()
        { ["Input.Title"] = "No picture", ["Input.Body"] = "Body", ["Input.RemoveImage"] = "true" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(imagePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Found, (await Upload(client, $"/hantera/nyheter/{article.Id}", png)).StatusCode);
        await Post(client, $"/hantera/nyheter/{article.Id}/ta-bort", "news-delete", new() { ["Input.Confirmed"] = "true" });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(imagePath)).StatusCode);
    }

    [Fact]
    public async Task Validation_retry_cannot_silently_discard_selected_image()
    {
        using var client = await Login(AccessLevels.KeyMember);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9uoAAAAASUVORK5CYII=");
        var invalid = await Upload(client, "/hantera/nyheter/ny", png, "");
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        var html = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("name=\"Input.SelectedImageName\" value=\"device-picture.png\"", html);
        Assert.Empty(await Articles());
        // Browsers clear file inputs after the validation response. Correcting the
        // description must not publish the article without the selected picture.
        var retry = await Post(client, "/hantera/nyheter/ny", "news-edit", new()
        {
            ["Input.Title"] = "News", ["Input.Body"] = "Body",
            ["Input.ImageDescription"] = "Picture", ["Input.SelectedImageName"] = "device-picture.png"
        });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Contains("Bilden följde inte med", WebUtility.HtmlDecode(await retry.Content.ReadAsStringAsync()));
        Assert.Empty(await Articles());
        Assert.Equal(HttpStatusCode.Found, (await Upload(client, "/hantera/nyheter/ny", png)).StatusCode);
        var article = Assert.Single(await Articles());
        foreach (var path in new[] { "/", "/nyheter" })
        {
            var page = await client.GetStringAsync(path);
            Assert.Contains($"src=\"{article.DisplayImageUrl}\"", page);
        }
        using var visitor = NewClient();
        Assert.Equal(png, await visitor.GetByteArrayAsync(article.DisplayImageUrl));
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string path, byte[] image, string description = "Picture")
    {
        var html = await client.GetStringAsync(path);
        var renderedForm = Regex.Match(html, "<form\\b[^>]*>").Value;
        Assert.Contains("enctype=\"multipart/form-data\"", renderedForm);
        Assert.Single(Regex.Matches(renderedForm, "enctype="));
        var fileInput = Regex.Match(html, "<input[^>]*type=\"file\"[^>]*>").Value;
        Assert.Contains("name=\"newsImage\"", fileInput);
        Assert.DoesNotContain("formenctype=\"application/x-www-form-urlencoded\"", html);
        using var form = new MultipartFormDataContent();
        foreach (var name in new[] { "__RequestVerificationToken", "Input.Version" })
        {
            var match = Regex.Match(html, "name=\"" + Regex.Escape(name) + "\" value=\"([^\"]*)\"");
            if (match.Success) form.Add(new StringContent(WebUtility.HtmlDecode(match.Groups[1].Value)), name);
        }
        form.Add(new StringContent("news-edit"), "_handler");
        form.Add(new StringContent("Uploaded image"), "Input.Title");
        form.Add(new StringContent("Body"), "Input.Body");
        form.Add(new StringContent(description), "Input.ImageDescription");
        form.Add(new ByteArrayContent(image), "newsImage", "device-picture.png");
        return await client.PostAsync(path, form);
    }

    private async Task<List<NewsArticle>> Articles()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().NewsArticles.AsNoTracking().ToListAsync();
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string handler, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(path);
        foreach (var name in new[] { "__RequestVerificationToken", "Input.Version" })
        {
            var match = Regex.Match(html, "name=\"" + Regex.Escape(name) + "\" value=\"([^\"]*)\"");
            if (match.Success) fields[name] = WebUtility.HtmlDecode(match.Groups[1].Value);
        }
        fields["_handler"] = handler;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    public void Dispose() => factory.Dispose();
}
