using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WDWAPP.Data;
using WDWAPP.Security;
using WDWAPP.Services;
using Xunit;

namespace WDWAPP.Tests;

public sealed partial class PlayerProfileTests : IDisposable
{
    private readonly AuthenticationFactory factory = new();
    private sealed record Actor(string Id, string Email, ClaimsPrincipal Principal);
    private async Task<Actor> User(string? role = AccessLevels.Member)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var key = Guid.NewGuid().ToString("N");
        var user = new ApplicationUser { UserName = "P" + key, Email = key + "@example.test", FullName = "Testspelare Efternamn" };
        Assert.True((await users.CreateAsync(user, "Test-password42!")).Succeeded);
        if (role is not null) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return new(user.Id, user.Email, await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user));
    }
    private async Task<PlayerProfileResult> Save(Actor actor, int? id = null, PlayerProfileInput? input = null, IFormFile? image = null)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlayerProfileService>().SaveAsync(actor.Principal, id, input ?? new() { DisplayName = "Testspelare" }, image);
    }
    private async Task<PlayerProfileResult> Delete(Actor actor, PlayerProfile profile)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlayerProfileService>().DeleteAsync(actor.Principal, profile.Id, profile.Version, true);
    }
    private async Task<PlayerProfile> Profile(int id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PlayerProfiles.AsNoTracking().SingleAsync(p => p.Id == id);
    }
    private static PlayerProfileInput Edit(PlayerProfile p) => new() { DisplayName = "Ändrat namn", Version = p.Version, Avatar = p.Avatar };
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private async Task<HttpClient> Login(Actor actor)
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/login", "login", new() { ["Input.Email"] = actor.Email, ["Input.Password"] = "Test-password42!" })).StatusCode);
        return client;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string handler, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(path);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        fields["_handler"] = handler;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    [Theory]
    [InlineData(AccessLevels.Member)]
    [InlineData(AccessLevels.KeyMember)]
    [InlineData(AccessLevels.Admin)]
    public async Task Members_can_create_exactly_one_profile_for_themselves(string role)
    {
        var member = await User(role);
        var result = await Save(member);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(member.Id, (await Profile(result.Id!.Value)).UserId);
        Assert.False((await Save(member)).Succeeded);
    }
    [Fact]
    public async Task Non_members_and_anonymous_users_cannot_create()
    {
        var visitor = await User(null);
        Assert.False((await Save(visitor)).Succeeded);
        Assert.False((await Save(new("", "", new ClaimsPrincipal(new ClaimsIdentity())))).Succeeded);
        using var client = await Login(visitor);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/spelare/ny")).StatusCode);
    }
    [Fact]
    public async Task Owners_can_edit_and_delete_but_other_members_cannot()
    {
        var owner = await User(); var other = await User();
        var id = (await Save(owner)).Id!.Value;
        var profile = await Profile(id);
        Assert.False((await Save(other, id, Edit(profile))).Succeeded);
        Assert.False((await Delete(other, profile)).Succeeded);
        Assert.True((await Save(owner, id, Edit(profile))).Succeeded);
        Assert.False((await Save(owner, id, Edit(profile))).Succeeded); // stale version
        Assert.Equal("Testspelare", (await Profile(id)).DisplayName);
        Assert.True((await Delete(owner, await Profile(id))).Succeeded);
        using var client = Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/spelare/{id}")).StatusCode);
    }
    [Fact]
    public async Task Admin_can_edit_and_delete_another_profile()
    {
        var owner = await User(); var admin = await User(AccessLevels.Admin);
        var id = (await Save(owner)).Id!.Value;
        Assert.True((await Save(admin, id, Edit(await Profile(id)))).Succeeded);
        using var client = await Login(admin);
        Assert.Contains("Redigera profil", await client.GetStringAsync("/spelare"));
        Assert.True((await Delete(admin, await Profile(id))).Succeeded);
    }
    [Fact]
    public async Task Former_member_can_delete_existing_profile_but_cannot_create_again()
    {
        var owner = await User(); var id = (await Save(owner)).Id!.Value;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(owner.Id))!;
            Assert.True((await users.RemoveFromRoleAsync(user, AccessLevels.Member)).Succeeded);
            owner = owner with { Principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user) };
        }
        Assert.True((await Delete(owner, await Profile(id))).Succeeded);
        Assert.False((await Save(owner)).Succeeded);
    }
    [Fact]
    public async Task Public_roster_and_detail_hide_zero_titles_and_do_not_expose_account_data()
    {
        var owner = await User(); var id = (await Save(owner)).Id!.Value;
        using var client = Client();
        foreach (var path in new[] { "/spelare" })
        {
            var html = await client.GetStringAsync(path);
            Assert.Contains("Testspelare", html); Assert.DoesNotContain("Sverige / Veizla", html); Assert.Contains("Matchstatistik:", html);
            Assert.DoesNotContain("WDW RTT:", html); Assert.DoesNotContain("40K-ligan:", html); Assert.DoesNotContain("Ligamästerskap:", html);
            Assert.DoesNotContain(owner.Email, html); Assert.DoesNotContain(owner.Id, html);
            Assert.DoesNotContain("Redigera profil", html);
        }
        var edit = Edit(await Profile(id)); edit.RttWins = 2;
        Assert.True((await Save(owner, id, edit)).Succeeded);
        foreach (var path in new[] { "/spelare" })
        {
            var html = await client.GetStringAsync(path);
            Assert.Contains("WDW RTT:", html); Assert.Contains("<strong>2</strong>", html);
            Assert.DoesNotContain("Ligamästerskap:", html);
        }
        var redirect = await client.GetAsync($"/spelare/{id}");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal($"https://localhost/spelare#spelare-{id}", redirect.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/spelare/2147483647")).StatusCode);
    }
    [Fact]
    public async Task Form_posts_bind_values_ignore_forged_ownership_and_require_antiforgery()
    {
        var owner = await User(); var other = await User();
        using var client = await Login(owner);
        var formHtml = await client.GetStringAsync("/spelare/ny");
        foreach (var avatar in PlayerProfileService.Avatars) Assert.Contains($"value=\"{avatar}\"", formHtml);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/spelare/ny", new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "player-edit" }))).StatusCode);
        var response = await Post(client, "/spelare/ny", "player-edit", new() {
            ["Input.DisplayName"] = "Formspelare", ["Input.FirstNameOnly"] = "true", ["Input.Avatar"] = "person-fill", ["Input.RttWins"] = "3", ["Input.UserId"] = other.Id });
        Assert.True(response.StatusCode == HttpStatusCode.Found, WebUtility.HtmlDecode(string.Join(" ", Regex.Matches(await response.Content.ReadAsStringAsync(), "<li[^>]*>(.*?)</li>").Select(m => m.Groups[1].Value))));
        var id = int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
        var profile = await Profile(id);
        Assert.Equal(owner.Id, profile.UserId); Assert.Equal(3, profile.SelfReportedTitles.RttWins);
        Assert.Equal("person-fill", profile.Avatar);
        Assert.Equal(1, profile.ImageZoom);
        Assert.Equal(50, profile.ImageX);
        Assert.Equal(25, profile.ImageY);
        using var stranger = await Login(other);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/spelare/{id}/redigera")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/spelare/{id}/radera")).StatusCode);
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/spelare/{id}/redigera", "player-edit", new() {
            ["Input.DisplayName"] = "Uppdaterad", ["Input.FirstNameOnly"] = "true", ["Input.Version"] = profile.Version, ["Input.Avatar"] = "question-circle" })).StatusCode);
        profile = await Profile(id);
        Assert.Equal("Testspelare", profile.DisplayName);
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/spelare/{id}/radera", "player-delete", new() {
            ["Input.Version"] = profile.Version, ["Input.Confirmed"] = "true" })).StatusCode);
    }
    [Fact]
    public async Task Invalid_counts_and_avatar_paths_are_rejected_server_side()
    {
        var owner = await User();
        foreach (var input in new[] { new PlayerProfileInput { RttWins = -1 }, new() { DisplayName = "A", LeagueWins = 10001 },
            new() { DisplayName = "A", ChampionshipWins = -1 }, new() { DisplayName = "A", Avatar = "../../file" } })
            Assert.False((await Save(owner, input: input)).Succeeded);
    }
    private static IFormFile File(byte[] bytes, string filename = "../../unsafe.exe") => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "playerImage", filename);
    [Fact]
    public async Task Images_use_detected_type_and_are_replaced_and_deleted_without_orphans()
    {
        var owner = await User();
        Assert.False((await Save(owner, image: File("<svg onload='alert(1)'/>"u8.ToArray(), "fake.png"))).Succeeded);
        Assert.False((await Save(owner, image: File(new byte[NewsImage.MaxBytes + 1]))).Succeeded);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1sAAAAASUVORK5CYII=");
        var result = await Save(owner, image: File(png));
        Assert.True(result.Succeeded, result.Message);
        var id = result.Id!.Value;
        using var client = Client();
        var response = await client.GetAsync($"/spelare/{id}/bild");
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
        var edit = Edit(await Profile(id)); edit.RemoveImage = true;
        Assert.True((await Save(owner, id, edit)).Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/spelare/{id}/bild")).StatusCode);
        Assert.Null((await Profile(id)).ImageData);
        Assert.True((await Save(owner, id, Edit(await Profile(id)), File(png))).Succeeded);
        Assert.True((await Delete(owner, await Profile(id))).Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/spelare/{id}/bild")).StatusCode);
    }
    [Fact]
    public async Task Migration_preserves_existing_calendar_news_and_enforces_profile_uniqueness_and_cleanup()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        await migrator.MigrateAsync("20260918124531_AddLeagueMatchDetails");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Events (Id, Title, Description, Date, Time, CreatedUtc, Version) VALUES (1, 'Existing event', 'Keep me', '2026-09-20', '18:00:00', '2026-09-19', 'v');
            INSERT INTO NewsArticles (Title, Body, Links, PublishedUtc, Version, EventId) VALUES ('Existing news', 'Keep news', '', '2026-09-19', 'n', 1);
            """);
        await migrator.MigrateAsync();
        var item = await db.Events.SingleAsync();
        Assert.Equal("Existing event", item.Title); Assert.Null(item.LeagueSessionId);
        Assert.Equal(1, (await db.NewsArticles.SingleAsync()).EventId);
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "Owner" });
        await db.SaveChangesAsync();
        db.PlayerProfiles.Add(new() { UserId = "owner", DisplayName = "Profile", ImageData = [1, 2, 3] });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); // Exercise the database constraint, without EF replacing a tracked one-to-one dependent.
        db.PlayerProfiles.Add(new() { UserId = "owner", DisplayName = "Duplicate" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await db.Users.Where(u => u.Id == "owner").ExecuteDeleteAsync();
        Assert.Empty(await db.PlayerProfiles.ToListAsync());
        Assert.Single(await db.Events.ToListAsync());
        Assert.Single(await db.NewsArticles.ToListAsync());
    }
    public void Dispose() => factory.Dispose();
}
