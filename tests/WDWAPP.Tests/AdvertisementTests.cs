using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WDWAPP.Data;
using WDWAPP.Security;
using WDWAPP.Services;
using Xunit;

namespace WDWAPP.Tests;

public sealed class AdvertisementTests : IDisposable
{
    private readonly AuthenticationFactory baseFactory = new();
    private readonly WebApplicationFactory<Program> factory;
    private readonly TestClock clock = new();
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2027, 12, 31, 17, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public AdvertisementTests()
    {
        factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.AddSingleton<TimeProvider>(clock);
            // Exercise deletion explicitly, so expiry visibility tests cannot race the cleanup worker.
            var worker = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AdvertisementCleanup));
            services.Remove(worker);
        }));
    }
    private sealed record Actor(string Id, string Email, ClaimsPrincipal Principal);
    private async Task<Actor> User(string? role = AccessLevels.Member)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var suffix = Guid.NewGuid().ToString("N");
        var user = new ApplicationUser { UserName = "P" + suffix, Email = suffix + "@example.test", PhoneNumber = "+46701234567", FullName = "Private Account Name" };
        Assert.True((await users.CreateAsync(user, "Test-password42!")).Succeeded);
        if (role is not null) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return new(user.Id, user.Email, await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user));
    }
    private static AdvertisementInput Valid() => new() { Title = "Orks till salu", Description = "En armé i fint skick.", AtVenueSelected = true };
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1sAAAAASUVORK5CYII=");
    private static IFormFile Image(byte[]? data = null, long? length = null) => new FormFile(new MemoryStream(data ?? Png), 0, length ?? (data ?? Png).Length, "advertisementImages", "../../malicious.exe");
    private async Task<AdvertisementResult> Save(Actor actor, int? id = null, AdvertisementInput? input = null, params IFormFile[] images)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdvertisementService>().SaveAsync(actor.Principal, id, input ?? Valid(), images);
    }
    private async Task<Advertisement> Ad(int id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Advertisements.AsNoTracking().Include(a => a.Contacts).Include(a => a.Images).SingleAsync(a => a.Id == id);
    }
    private async Task<AdvertisementResult> Delete(Actor actor, Advertisement ad, bool confirmed = true)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdvertisementService>().DeleteAsync(actor.Principal, ad.Id, ad.Version, confirmed);
    }
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private async Task<HttpClient> Login(Actor actor)
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/login", "login", new() { ["Input.Email"] = actor.Email, ["Input.Password"] = "Test-password42!" })).StatusCode);
        return client;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string handler, Dictionary<string, string> fields, params byte[][] images)
    {
        var html = await client.GetStringAsync(path);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        fields["_handler"] = handler;
        using var form = new MultipartFormDataContent();
        foreach (var field in fields) form.Add(new StringContent(field.Value), field.Key);
        foreach (var image in images) form.Add(new ByteArrayContent(image), "advertisementImages", "../../unsafe.exe");
        return await client.PostAsync(path, form);
    }
    private static Dictionary<string, string> Fields() => new() { ["Input.Title"] = "Formannons", ["Input.Description"] = "Beskrivning", ["Input.Type"] = "Sell", ["Input.AtVenueSelected"] = "true" };

    [Theory]
    [InlineData(AccessLevels.Member)]
    [InlineData(AccessLevels.KeyMember)]
    [InlineData(AccessLevels.Admin)]
    public async Task Member_levels_can_create_with_server_owned_identity_and_calendar_month_expiry(string role)
    {
        var owner = await User(role);
        var result = await Save(owner);
        Assert.True(result.Succeeded, result.Message);
        var ad = await Ad(result.Id!.Value);
        Assert.Equal(owner.Id, ad.OwnerUserId);
        Assert.Equal(clock.Now.UtcDateTime, ad.CreatedAt);
        Assert.Equal(new DateTime(2028, 2, 29, 17, 0, 0, DateTimeKind.Utc), ad.ExpiresAt);
        Assert.Equal(ad.CreatedAt, ad.UpdatedAt);
        Assert.Empty(ad.Images);
        Assert.Equal(AdvertisementContactType.AtVenue, Assert.Single(ad.Contacts).Type);
        Assert.Null(ad.Contacts[0].Value);
    }
    [Fact]
    public async Task Public_and_non_member_cannot_create_or_access_create_form()
    {
        var nonmember = await User(null);
        Assert.False((await Save(nonmember)).Succeeded);
        Assert.False((await Save(new("", "", new ClaimsPrincipal(new ClaimsIdentity())))).Succeeded);
        using var visitor = Client(); using var client = await Login(nonmember);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/marknad/ny")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/marknad/ny")).StatusCode);
        Assert.DoesNotContain("Sätt in annons", WebUtility.HtmlDecode(await visitor.GetStringAsync("/marknad")));
    }
    [Theory]
    [InlineData(AdvertisementContactType.Email, "", false)]
    [InlineData(AdvertisementContactType.Email, "not-an-email", false)]
    [InlineData(AdvertisementContactType.Email, "advert@example.test", true)]
    [InlineData(AdvertisementContactType.Phone, " ", false)]
    [InlineData(AdvertisementContactType.Phone, "abc", false)]
    [InlineData(AdvertisementContactType.Phone, "+46 70 123 45 67", true)]
    [InlineData(AdvertisementContactType.Sms, "", false)]
    [InlineData(AdvertisementContactType.Sms, "0701234567", true)]
    [InlineData(AdvertisementContactType.Messenger, " ", false)]
    [InlineData(AdvertisementContactType.Messenger, "My Messenger Name", true)]
    [InlineData(AdvertisementContactType.Discord, "", false)]
    [InlineData(AdvertisementContactType.Discord, "dice_warrior", true)]
    [InlineData(AdvertisementContactType.AtVenue, null, true)]
    public async Task Contacts_are_conditionally_validated_on_the_server(AdvertisementContactType type, string? value, bool valid)
    {
        var owner = await User();
        var input = AdvertisementInput.From(new Advertisement { Title = "Contact", Description = "Details", Contacts = [new() { Type = type, Value = value }] });
        Assert.Equal(valid, (await Save(owner, input: input)).Succeeded);
    }
    [Fact]
    public async Task No_contact_invalid_types_and_invalid_lengths_are_rejected()
    {
        var owner = await User();
        var input = Valid(); input.AtVenueSelected = false;
        Assert.False((await Save(owner, input: input)).Succeeded);
        input = Valid(); input.Type = (AdvertisementType)99;
        Assert.False((await Save(owner, input: input)).Succeeded);
        input = Valid(); input.Title = " ";
        Assert.False((await Save(owner, input: input)).Succeeded);
        input = Valid(); input.Description = new string('x', 10001);
        Assert.False((await Save(owner, input: input)).Succeeded);
        input = Valid(); input.DiscordSelected = true; input.Discord = new string('x', 255);
        Assert.False((await Save(owner, input: input)).Succeeded);
    }
    [Fact]
    public async Task Public_pages_encode_content_and_publish_only_selected_explicit_contacts()
    {
        var owner = await User();
        var input = Valid(); input.Title = "<script>alert(1)</script>"; input.Description = "<img src=x onerror=alert(1)>";
        input.Email = "not-selected@example.test"; input.DiscordSelected = true; input.Discord = "<script>discord</script>";
        var id = (await Save(owner, input: input)).Id!.Value;
        using var visitor = Client();
        foreach (var path in new[] { "/marknad" })
        {
            var html = await visitor.GetStringAsync(path);
            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.DoesNotContain("<img src=x", html);
            Assert.DoesNotContain(owner.Email, html); Assert.DoesNotContain(owner.Id, html);
            Assert.DoesNotContain("Private Account Name", html); Assert.DoesNotContain("+46701234567", html);
            Assert.DoesNotContain("not-selected@example.test", html);
            Assert.DoesNotContain($"/marknad/{id}/redigera", html);
        }
        Assert.DoesNotContain("&lt;script&gt;discord&lt;/script&gt;", await visitor.GetStringAsync("/marknad"));
        using var signedIn = await Login(await User());
        Assert.Contains("&lt;script&gt;discord&lt;/script&gt;", await signedIn.GetStringAsync("/marknad"));
        var redirect = await visitor.GetAsync($"/marknad/{id}");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.EndsWith($"#annons-{id}", redirect.Headers.Location!.OriginalString);
        Assert.Equal(2, (await Ad(id)).Contacts.Count);
    }
    [Fact]
    public async Task Owners_can_edit_and_delete_but_not_other_users_ads_or_images()
    {
        var owner = await User(); var other = await User();
        var id = (await Save(owner, images: [Image()])).Id!.Value;
        var otherId = (await Save(other, images: [Image()])).Id!.Value;
        var original = await Ad(id);
        var edit = AdvertisementInput.From(original); edit.Title = "Changed";
        Assert.False((await Save(other, id, edit)).Succeeded);
        Assert.False((await Delete(other, original)).Succeeded);
        edit.RemoveImageIds = [(await Ad(otherId)).Images[0].Id];
        Assert.False((await Save(owner, id, edit)).Succeeded);
        Assert.Single((await Ad(otherId)).Images);
        edit.RemoveImageIds = [];
        clock.Now = clock.Now.AddDays(10);
        Assert.True((await Save(owner, id, edit)).Succeeded);
        var updated = await Ad(id);
        Assert.Equal(original.OwnerUserId, updated.OwnerUserId);
        Assert.Equal(original.CreatedAt, updated.CreatedAt); Assert.Equal(original.ExpiresAt, updated.ExpiresAt);
        Assert.Equal(clock.Now.UtcDateTime, updated.UpdatedAt);
        Assert.False((await Save(owner, id, edit)).Succeeded); // stale edit
        Assert.False((await Delete(owner, original)).Succeeded); // stale delete
        Assert.False((await Delete(owner, updated, false)).Succeeded);
        Assert.True((await Delete(owner, updated)).Succeeded);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AdvertisementImages.AnyAsync(i => i.AdvertisementId == id));
        Assert.False(await db.AdvertisementContacts.AnyAsync(c => c.AdvertisementId == id));
        Assert.True(await db.Advertisements.AnyAsync(a => a.Id == otherId));
    }
    [Fact]
    public async Task Admin_can_edit_delete_any_ad_and_form_access_is_checked()
    {
        var owner = await User(); var admin = await User(AccessLevels.Admin); var other = await User(AccessLevels.KeyMember);
        var id = (await Save(owner)).Id!.Value;
        using var stranger = await Login(other); using var manager = await Login(admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/marknad/{id}/redigera")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/marknad/{id}/radera")).StatusCode);
        Assert.DoesNotContain("Orks till salu", await stranger.GetStringAsync("/marknad/mina"));
        Assert.Contains("Orks till salu", await manager.GetStringAsync("/marknad/mina"));
        Assert.Contains($"/marknad/{id}/redigera", await manager.GetStringAsync("/marknad"));
        var input = AdvertisementInput.From(await Ad(id)); input.Title = "Admin edit";
        Assert.True((await Save(admin, id, input)).Succeeded);
        Assert.True((await Delete(admin, await Ad(id))).Succeeded);
    }
    [Fact]
    public async Task Former_member_can_manage_existing_ads_but_cannot_create_new_ones()
    {
        var owner = await User(); var id = (await Save(owner)).Id!.Value;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(owner.Id))!;
            Assert.True((await users.RemoveFromRoleAsync(user, AccessLevels.Member)).Succeeded);
            owner = owner with { Principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user) };
        }
        Assert.True((await Save(owner, id, AdvertisementInput.From(await Ad(id)))).Succeeded);
        Assert.False((await Save(owner)).Succeeded);
        Assert.True((await Delete(owner, await Ad(id))).Succeeded);
    }
    [Fact]
    public async Task Images_enforce_count_size_signature_and_atomic_replacement()
    {
        var owner = await User();
        Assert.False((await Save(owner, images: Enumerable.Range(0, 11).Select(_ => Image()).ToArray())).Succeeded);
        Assert.False((await Save(owner, images: [Image(length: NewsImage.MaxBytes + 1)])).Succeeded);
        Assert.False((await Save(owner, images: [Image("<svg onload='alert(1)'/>"u8.ToArray())])).Succeeded);
        Assert.False((await Save(owner, images: [Image([])])).Succeeded);
        var result = await Save(owner, images: Enumerable.Range(0, 10).Select(_ => Image()).ToArray());
        Assert.True(result.Succeeded, result.Message);
        var id = result.Id!.Value;
        var ad = await Ad(id); Assert.Equal(10, ad.Images.Count);
        Assert.False((await Save(owner, id, AdvertisementInput.From(ad), Image())).Succeeded);
        var edit = AdvertisementInput.From(ad); edit.RemoveImageIds = [ad.Images.OrderBy(i => i.SortOrder).First().Id];
        Assert.False((await Save(owner, id, edit, Image("invalid"u8.ToArray()))).Succeeded);
        Assert.Equal(10, (await Ad(id)).Images.Count); // No removals persisted on failed validation.
        Assert.True((await Save(owner, id, edit, Image())).Succeeded);
        var updated = await Ad(id); Assert.Equal(10, updated.Images.Count);
        Assert.DoesNotContain(updated.Images, i => edit.RemoveImageIds.Contains(i.Id));
        using var client = Client();
        var primary = updated.Images.OrderBy(i => i.SortOrder).First();
        Assert.Contains($"/marknad/bilder/{primary.Id}", await client.GetStringAsync("/marknad"));
        var imageResponse = await client.GetAsync($"/marknad/bilder/{primary.Id}");
        Assert.Equal("image/png", imageResponse.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", imageResponse.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Png, await imageResponse.Content.ReadAsByteArrayAsync());
        edit = AdvertisementInput.From(updated); edit.RemoveImageIds = updated.Images.Select(i => i.Id).ToList();
        Assert.True((await Save(owner, id, edit)).Succeeded);
        Assert.Empty((await Ad(id)).Images);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/marknad/bilder/{primary.Id}")).StatusCode);
    }
    [Fact]
    public async Task Expired_ads_and_images_disappear_at_the_boundary_before_cleanup_and_cannot_be_extended()
    {
        var owner = await User(); var id = (await Save(owner, images: [Image()])).Id!.Value;
        var ad = await Ad(id); var imageId = Assert.Single(ad.Images).Id;
        using var visitor = Client();
        clock.Now = new DateTimeOffset(DateTime.SpecifyKind(ad.ExpiresAt, DateTimeKind.Utc)).AddTicks(-1);
        Assert.Equal(HttpStatusCode.Found, (await visitor.GetAsync($"/marknad/{id}")).StatusCode);
        clock.Now = clock.Now.AddTicks(1);
        Assert.DoesNotContain(ad.Title, await visitor.GetStringAsync("/marknad"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/marknad/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/marknad/bilder/{imageId}")).StatusCode);
        Assert.False((await Save(owner, id, AdvertisementInput.From(ad))).Succeeded);
        Assert.Equal(ad.ExpiresAt, (await Ad(id)).ExpiresAt);
        var activeId = (await Save(owner)).Id!.Value;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await AdvertisementCleanup.DeleteExpiredBatchAsync(db, clock.Now.UtcDateTime));
        Assert.False(await db.Advertisements.AnyAsync(a => a.Id == id));
        Assert.Empty(await db.AdvertisementImages.ToListAsync());
        Assert.False(await db.AdvertisementContacts.AnyAsync(c => c.AdvertisementId == id));
        Assert.True(await db.Advertisements.AnyAsync(a => a.Id == activeId));
        Assert.Equal(0, await AdvertisementCleanup.DeleteExpiredBatchAsync(db, clock.Now.UtcDateTime));
    }
    [Fact]
    public async Task Expired_ad_can_still_be_deleted_by_owner_before_cleanup()
    {
        var owner = await User(); var id = (await Save(owner)).Id!.Value;
        var ad = await Ad(id); clock.Now = clock.Now.AddMonths(3);
        using var client = await Login(owner);
        Assert.Contains("Utgången", WebUtility.HtmlDecode(await client.GetStringAsync("/marknad/mina")));
        Assert.True((await Delete(owner, ad)).Succeeded);
    }
    [Fact]
    public async Task Http_forms_bind_contacts_images_and_removals_but_ignore_owner_and_date_forgery()
    {
        var owner = await User(); var other = await User(); using var client = await Login(owner);
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/marknad/ny"));
        Assert.Contains("Kontaktuppgifterna visas för inloggade personer på sidan.", html);
        Assert.Contains("två månader efter publicering", html);
        foreach (var type in Enum.GetValues<AdvertisementType>()) Assert.Contains($"value=\"{type}\"", html);
        var fields = Fields(); fields["Input.OwnerUserId"] = other.Id; fields["Input.CreatedAt"] = "2000-01-01"; fields["Input.ExpiresAt"] = "2099-01-01";
        fields["Input.EmailSelected"] = "true"; fields["Input.Email"] = "explicit@example.test";
        var response = await Post(client, "/marknad/ny", "advertisement-edit", fields, Png, Png);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var id = int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
        var ad = await Ad(id);
        Assert.Equal(owner.Id, ad.OwnerUserId); Assert.Equal(clock.Now.UtcDateTime, ad.CreatedAt);
        Assert.Equal(ad.CreatedAt.AddMonths(2), ad.ExpiresAt); Assert.Equal(2, ad.Images.Count);
        Assert.Equal(2, ad.Contacts.Count); Assert.Contains(ad.Contacts, c => c.Value == "explicit@example.test");
        fields["Input.Title"] = "Edited form"; fields["Input.Type"] = "Trade"; fields["Input.Version"] = ad.Version;
        fields["Input.RemoveImageIds"] = ad.Images[0].Id.ToString();
        fields["Input.EmailSelected"] = "false"; fields["Input.DiscordSelected"] = "true"; fields["Input.Discord"] = "wdw-player";
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/marknad/{id}/redigera", "advertisement-edit", fields, Png)).StatusCode);
        var updated = await Ad(id);
        Assert.Equal(2, updated.Images.Count); Assert.DoesNotContain(updated.Images, i => i.Id == ad.Images[0].Id);
        Assert.Equal(ad.CreatedAt, updated.CreatedAt); Assert.Equal(ad.ExpiresAt, updated.ExpiresAt); Assert.Equal(owner.Id, updated.OwnerUserId);
        Assert.Equal(AdvertisementType.Trade, updated.Type);
        Assert.DoesNotContain(updated.Contacts, c => c.Type == AdvertisementContactType.Email);
        Assert.Contains(updated.Contacts, c => c.Type == AdvertisementContactType.Discord);
        var deletion = new Dictionary<string, string> { ["Input.Version"] = updated.Version };
        Assert.Equal(HttpStatusCode.OK, (await Post(client, $"/marknad/{id}/radera", "advertisement-delete", deletion)).StatusCode);
        deletion["Input.Confirmed"] = "true";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/marknad/{id}/radera", "advertisement-delete", deletion)).StatusCode);
    }
    [Fact]
    public async Task Http_posts_require_antiforgery_and_server_contact_validation()
    {
        var owner = await User(); using var client = await Login(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/marknad/ny", new FormUrlEncodedContent(Fields()))).StatusCode);
        var fields = Fields(); fields["Input.AtVenueSelected"] = "false"; fields["Input.EmailSelected"] = "true";
        var response = await Post(client, "/marknad/ny", "advertisement-edit", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Ange uppgifter", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Advertisements.ToListAsync());
    }
    [Fact]
    public async Task Migration_preserves_existing_data_and_account_deletion_cascades_ad_images_and_contacts()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        await migrator.MigrateAsync("20260919084816_AddPlayerProfilesAndLeagueCalendar");
        db.Users.Add(new() { Id = "owner", UserName = "Keep user" });
        db.NewsArticles.Add(new() { Title = "Keep news", Body = "Existing", PublishedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        // Seed using the schema at this migration, not the newer runtime EF model.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Events (Title, Description, Date, CreatedUtc, Version) VALUES ('Keep event', '', '2026-09-19', '2026-09-19', 'e');
            INSERT INTO PlayerProfiles (UserId, DisplayName, Avatar, Version, SelfReportedTitles_LeagueWins, SelfReportedTitles_RttWins, SelfReportedTitles_ChampionshipWins)
                VALUES ('owner', 'Keep profile', 'person-circle', 'p', 0, 0, 0);
            """);
        await migrator.MigrateAsync();
        Assert.Equal("Keep event", (await db.Events.SingleAsync()).Title);
        Assert.Equal("Keep news", (await db.NewsArticles.SingleAsync()).Title);
        Assert.Equal("Keep profile", (await db.PlayerProfiles.SingleAsync()).DisplayName);
        db.Advertisements.Add(new() { OwnerUserId = "owner", Title = "New", Description = "Details", CreatedAt = clock.Now.UtcDateTime,
            ExpiresAt = clock.Now.AddMonths(2).UtcDateTime, Images = [new() { Data = Png, ContentType = "image/png" }], Contacts = [new() { Type = AdvertisementContactType.AtVenue }] });
        await db.SaveChangesAsync();
        await db.Users.Where(u => u.Id == "owner").ExecuteDeleteAsync();
        Assert.Empty(await db.Advertisements.ToListAsync()); Assert.Empty(await db.AdvertisementImages.ToListAsync()); Assert.Empty(await db.AdvertisementContacts.ToListAsync());
        Assert.Single(await db.Events.ToListAsync()); Assert.Single(await db.NewsArticles.ToListAsync());
    }
    [Fact]
    public async Task Listing_shows_full_text_all_images_and_contact_dialog_and_edit_appends_images()
    {
        var owner = await User();
        var input = Valid(); input.Description = new string('x', 500) + " Slutet av hela annonsen";
        input.DiscordSelected = true; input.Discord = "contact-player";
        var id = (await Save(owner, input: input, images: [Image()])).Id!.Value;
        var original = await Ad(id);
        using var client = await Login(owner);
        var fields = Fields(); fields["Input.Description"] = input.Description;
        fields["Input.DiscordSelected"] = "true"; fields["Input.Discord"] = input.Discord;
        fields["Input.Version"] = original.Version;
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/marknad/{id}/redigera", "advertisement-edit", fields, Png)).StatusCode);
        var updated = await Ad(id);
        Assert.Equal(2, updated.Images.Count);
        Assert.Contains(updated.Images, image => image.Id == original.Images[0].Id);
        using var visitor = Client();
        var html = WebUtility.HtmlDecode(await visitor.GetStringAsync("/marknad"));
        Assert.Contains("<h1>Marknad</h1>", html);
        Assert.DoesNotContain("Sälj, köp, byt eller ge bort", html);
        Assert.Contains(input.Description, html);
        foreach (var image in updated.Images) Assert.Contains($"data-market-image=\"/marknad/bilder/{image.Id}\"", html);
        Assert.Contains($"data-market-dialog=\"kontakt-{id}\"", html);
        Assert.DoesNotContain("contact-player", html);
        Assert.Contains("contact-player", await client.GetStringAsync("/marknad"));
        Assert.Contains("market-card-footer", html);
        Assert.DoesNotContain($"href=\"/marknad/{id}\"", html);
    }
    public void Dispose() { factory.Dispose(); baseFactory.Dispose(); }
}
