using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WDWAPP.Data;
using WDWAPP.Security;
using WDWAPP.Services;
using Xunit;

namespace WDWAPP.Tests;

public sealed class AdminTests : IDisposable
{
    private readonly AuthenticationFactory factory = new();
    private const string Password = "Admin-test42!";

    [Fact]
    public async Task Only_admins_can_see_menu_and_access_admin_pages()
    {
        using var client = NewClient();
        Assert.DoesNotContain("href=\"/admin\"", await client.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
        var user = await CreateUser();
        await Login(client, user.Email!);
        Assert.DoesNotContain("href=\"/admin\"", await client.GetStringAsync("/"));
        foreach (var path in new[] { "/admin", $"/admin/users/{user.Id}", $"/admin/users/{user.Id}/delete" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/access-denied", response.Headers.Location!.ToString());
            var post = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        }
        Assert.DoesNotContain("Behörighet:", WebUtility.HtmlDecode(await client.GetStringAsync("/account")));
        var registration = WebUtility.HtmlDecode(await client.GetStringAsync("/register"));
        Assert.Contains("Nickname", registration);
        Assert.DoesNotContain("smeknamn", registration, StringComparison.OrdinalIgnoreCase);

        var admin = await CreateUser(AccessLevels.Admin);
        using var adminClient = NewClient();
        await Login(adminClient, admin.Email!);
        Assert.Contains("href=\"/hantera\"", await adminClient.GetStringAsync("/"));
        Assert.Contains("href=\"/admin\"", await adminClient.GetStringAsync("/hantera"));
        Assert.Contains(user.Email!, await adminClient.GetStringAsync("/admin"));
    }

    [Fact]
    public async Task Admin_can_change_levels_and_delete_with_confirmation_and_antiforgery()
    {
        var admin = await CreateUser(AccessLevels.Admin);
        var user = await CreateUser();
        using var adminClient = NewClient();
        using var userClient = NewClient();
        await Login(adminClient, admin.Email!);
        await Login(userClient, user.Email!);
        var path = $"/admin/users/{user.Id}";

        // Forged requests cannot change roles.
        var forged = await adminClient.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["_handler"] = "edit-user", ["Input.Level"] = AccessLevels.Admin, ["Input.Version"] = user.ConcurrencyStamp! }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        foreach (var level in new[] { AccessLevels.Member, AccessLevels.KeyMember, AccessLevels.Admin, AccessLevels.LoggedIn })
        {
            var response = await PostForm(adminClient, path, "edit-user", new() { ["Input.Level"] = level });
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var current = await users.FindByIdAsync(user.Id);
            var roles = await users.GetRolesAsync(current!);
            if (level == AccessLevels.LoggedIn) Assert.Empty(roles);
            else Assert.Equal(new[] { level }, roles);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await userClient.GetAsync("/account")).StatusCode);
        await Login(userClient, user.Email!);

        var notConfirmed = await PostForm(adminClient, path + "/delete", "delete-user", new());
        Assert.Equal(HttpStatusCode.OK, notConfirmed.StatusCode);
        Assert.Contains("Bekräfta", WebUtility.HtmlDecode(await notConfirmed.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/account")).StatusCode);

        var deleted = await PostForm(adminClient, path + "/delete", "delete-user", new() { ["Input.Confirmed"] = "true" });
        Assert.Equal(HttpStatusCode.Found, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await userClient.GetAsync("/account")).StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.Null(await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(user.Id));
    }

    [Fact]
    public async Task Demoted_admin_cannot_use_old_cookie_or_stale_principal()
    {
        var admin = await CreateUser(AccessLevels.Admin);
        var otherAdmin = await CreateUser(AccessLevels.Admin);
        var target = await CreateUser();
        using var client = NewClient();
        using var otherClient = NewClient();
        await Login(client, admin.Email!);
        await Login(otherClient, otherAdmin.Email!);
        var oldForm = await otherClient.GetStringAsync($"/admin/users/{target.Id}");
        var oldPrincipal = await Principal(otherAdmin);

        var response = await PostForm(client, $"/admin/users/{otherAdmin.Id}", "edit-user", new()
        { ["Input.Level"] = AccessLevels.Member });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var stalePost = await otherClient.PostAsync($"/admin/users/{target.Id}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "edit-user", ["__RequestVerificationToken"] = Field(oldForm, "__RequestVerificationToken"),
            ["Input.Version"] = Field(oldForm, "Input.Version"), ["Input.Level"] = AccessLevels.Admin
        }));
        Assert.Equal(HttpStatusCode.Redirect, stalePost.StatusCode);
        Assert.Contains("/login", stalePost.Headers.Location!.ToString());
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AdminUserService>()
            .ChangeLevelAsync(oldPrincipal, target.Id, target.ConcurrencyStamp!, AccessLevels.Admin);
        Assert.False(result.Succeeded);
        await Login(otherClient, otherAdmin.Email!);
        Assert.Contains("/access-denied", (await otherClient.GetAsync("/admin")).Headers.Location!.ToString());
    }

    [Fact]
    public async Task Self_changes_invalid_roles_and_stale_edits_are_rejected()
    {
        var admin = await CreateUser(AccessLevels.Admin);
        var user = await CreateUser();
        var principal = await Principal(admin);
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AdminUserService>();
            Assert.False((await service.ChangeLevelAsync(principal, admin.Id, admin.ConcurrencyStamp!, AccessLevels.LoggedIn)).Succeeded);
            Assert.False((await service.DeleteAsync(principal, admin.Id, admin.ConcurrencyStamp!, true)).Succeeded);
            Assert.False((await service.ChangeLevelAsync(principal, user.Id, user.ConcurrencyStamp!, "SuperAdmin")).Succeeded);
            Assert.False((await service.DeleteAsync(principal, user.Id, user.ConcurrencyStamp!, false)).Succeeded);
            Assert.True((await service.ChangeLevelAsync(principal, user.Id, user.ConcurrencyStamp!, AccessLevels.Member)).Succeeded);
            Assert.False((await service.ChangeLevelAsync(principal, user.Id, user.ConcurrencyStamp!, AccessLevels.Admin)).Succeeded);
            Assert.False((await service.DeleteAsync(principal, user.Id, user.ConcurrencyStamp!, true)).Succeeded);
        }
    }

    [Fact]
    public async Task Initial_admin_requires_existing_account_and_only_runs_once()
    {
        var user = await CreateUser(AccessLevels.Member);
        var second = await CreateUser();
        using var scope = factory.Services.CreateScope();
        var setup = scope.ServiceProvider.GetRequiredService<InitialAdminSetup>();
        Assert.False((await setup.PromoteAsync("missing@example.test")).Succeeded);
        Assert.True((await setup.PromoteAsync(user.Email!)).Succeeded);
        Assert.False((await setup.PromoteAsync(second.Email!)).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Single(await users.GetUsersInRoleAsync(AccessLevels.Admin));
        Assert.Equal(new[] { AccessLevels.Admin }, await users.GetRolesAsync((await users.FindByIdAsync(user.Id))!));
        Assert.False(await users.IsInRoleAsync((await users.FindByIdAsync(second.Id))!, AccessLevels.Admin));
    }

    [Fact]
    public async Task User_cards_search_and_member_list_use_real_names_and_include_all_registration_levels()
    {
        var admin = await CreateUser(AccessLevels.Admin);
        var member = await CreateUser(AccessLevels.Member);
        var keyMember = await CreateUser(AccessLevels.KeyMember);
        var ordinary = await CreateUser();
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByIdAsync(member.Id))!;
            user.FullName = "Åsa Öberg";
            Assert.True((await manager.UpdateAsync(user)).Succeeded);
        }
        using var client = NewClient();
        await Login(client, admin.Email!);
        Assert.DoesNotContain("href=\"/admin/medlemmar\"", await client.GetStringAsync("/hantera"));
        var cards = WebUtility.HtmlDecode(await client.GetStringAsync("/admin"));
        Assert.Contains("href=\"/admin/medlemmar\"", cards);
        Assert.Contains("<h3>Åsa Öberg</h3>", cards);
        Assert.DoesNotContain($"<h3>{member.UserName}</h3>", cards);
        Assert.Contains("Sök namn eller e-post", cards);
        var nameSearch = await client.GetStringAsync("/admin?Search=" + Uri.EscapeDataString("åsa ö"));
        Assert.Contains($"/admin/users/{member.Id}", nameSearch);
        Assert.DoesNotContain($"/admin/users/{ordinary.Id}", nameSearch);
        // The nickname is also part of this test account's email, so give it a separate nickname.
        using (var scope = factory.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByIdAsync(member.Id))!;
            user.UserName = "OnlyNickname";
            Assert.True((await manager.UpdateAsync(user)).Succeeded);
        }
        Assert.DoesNotContain($"/admin/users/{member.Id}", await client.GetStringAsync("/admin?Search=OnlyNickname"));
        Assert.Contains($"/admin/users/{member.Id}", await client.GetStringAsync("/admin?Search=" + Uri.EscapeDataString(member.Email!)));
        var list = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/medlemmar"));
        Assert.Contains("<table>", list);
        Assert.Contains("Åsa Öberg", list);
        foreach (var user in new[] { admin, member, keyMember, ordinary }) Assert.Contains($"/admin/users/{user.Id}", list);
        foreach (var level in new[] { "Registrerad", "Terminspassinnehavare", "Nyckelmedlem", "Administratör" })
            Assert.Contains($"<td>{level}</td>", list);
        foreach (var level in new[] { AccessLevels.LoggedIn, AccessLevels.Member, AccessLevels.KeyMember, AccessLevels.Admin })
            Assert.Matches("<input[^>]*name=\"Levels\"[^>]*value=\"" + level + "\"[^>]*checked", list);
        var combined = await client.GetStringAsync("/admin/medlemmar?Filtered=true&Levels=LoggedIn&Levels=KeyMember");
        Assert.Contains($"/admin/users/{ordinary.Id}", combined);
        Assert.Contains($"/admin/users/{keyMember.Id}", combined);
        Assert.DoesNotContain($"/admin/users/{member.Id}", combined);
        Assert.DoesNotContain($"/admin/users/{admin.Id}", combined);
        var single = await client.GetStringAsync("/admin/medlemmar?Filtered=true&Levels=Admin");
        Assert.Contains($"/admin/users/{admin.Id}", single);
        Assert.DoesNotContain($"/admin/users/{ordinary.Id}", single);
        Assert.Contains("Inga användare hittades", WebUtility.HtmlDecode(await client.GetStringAsync("/admin/medlemmar?Filtered=true")));
        var combinedSearch = await client.GetStringAsync("/admin/medlemmar?Filtered=true&Levels=Member&Levels=Admin&Search=" + Uri.EscapeDataString("åsa"));
        Assert.Contains($"/admin/users/{member.Id}", combinedSearch);
        Assert.DoesNotContain($"/admin/users/{admin.Id}", combinedSearch);
        var filtered = await client.GetStringAsync("/admin/medlemmar?Search=" + Uri.EscapeDataString("ÖBERG"));
        Assert.Contains($"/admin/users/{member.Id}", filtered);
        Assert.DoesNotContain($"/admin/users/{keyMember.Id}", filtered);
        Assert.Contains("Inga användare hittades", WebUtility.HtmlDecode(await client.GetStringAsync("/admin/medlemmar?Search=NoMatchingMember")));
        using var anonymous = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/admin/medlemmar")).StatusCode);
        using var keyClient = NewClient();
        await Login(keyClient, keyMember.Email!);
        Assert.Contains("/access-denied", (await keyClient.GetAsync("/admin/medlemmar")).Headers.Location!.ToString());
        Assert.DoesNotContain("href=\"/admin/medlemmar\"", await keyClient.GetStringAsync("/hantera"));
    }

    private async Task<ApplicationUser> CreateUser(string? role = null)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var name = "Test" + Guid.NewGuid().ToString("N")[..12];
        var user = new ApplicationUser { UserName = name, Email = name + "@example.test" };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (role is not null) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    [Fact]
    public async Task Admin_can_view_and_edit_all_account_details_including_own_account()
    {
        var admin = await CreateUser(AccessLevels.Admin);
        var target = await CreateUser();
        using var client = NewClient();
        await Login(client, admin.Email!);
        foreach (var user in new[] { target, admin })
        {
            var path = $"/admin/users/{user.Id}";
            var html = await client.GetStringAsync(path);
            var nickname = "Edited" + user.UserName;
            var fields = new Dictionary<string, string>
            {
                ["_handler"] = "edit-user-details", ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken"),
                ["Details.Version"] = Field(html, "Details.Version"), ["Details.Nickname"] = nickname,
                ["Details.Email"] = nickname + "@example.test", ["Details.FullName"] = " Anna Andersson ",
                ["Details.Address"] = " Storgatan 1 ", ["Details.PostalCode"] = "12345",
                ["Details.City"] = " Stockholm ", ["Details.PhoneNumber"] = "+46701234567"
            };
            var forged = new Dictionary<string, string>(fields);
            forged.Remove("__RequestVerificationToken");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, new FormUrlEncodedContent(forged))).StatusCode);
            var response = await client.PostAsync(path, new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var saved = (await users.FindByIdAsync(user.Id))!;
            Assert.Equal(nickname, saved.UserName);
            Assert.Equal(nickname + "@example.test", saved.Email);
            Assert.Equal("Anna Andersson", saved.FullName);
            Assert.Equal("Storgatan 1", saved.Address);
            Assert.Equal("123 45", saved.PostalCode);
            Assert.Equal("Stockholm", saved.City);
            Assert.Equal("+46701234567", saved.PhoneNumber);
            Assert.Equal(user.Id == admin.Id, await users.IsInRoleAsync(saved, AccessLevels.Admin));
            var updatedHtml = WebUtility.HtmlDecode(await client.GetStringAsync(path));
            foreach (var value in new[] { nickname, saved.Email, saved.FullName, saved.Address, saved.PostalCode, saved.City, saved.PhoneNumber })
                Assert.Contains(value!, updatedHtml);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_details_are_prefilled_and_changing_only_email_preserves_contact_details(bool ownAccount)
    {
        var admin = await CreateUser(AccessLevels.Admin);
        var target = ownAccount ? admin : await CreateUser();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(target.Id))!;
            user.FullName = "Anna Andersson";
            user.Address = "Storgatan 1";
            user.PostalCode = "123 45";
            user.City = "Stockholm";
            user.PhoneNumber = "+46701234567";
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        using var client = NewClient();
        await Login(client, admin.Email!);
        var path = $"/admin/users/{target.Id}";
        var html = await client.GetStringAsync(path);
        var fields = new Dictionary<string, string>();
        foreach (Match match in Regex.Matches(html, @"<input\b[^>]*>"))
        {
            var name = Regex.Match(match.Value, "name=\"([^\"]+)\"");
            var value = Regex.Match(match.Value, "value=\"([^\"]*)\"");
            if (name.Success) fields[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(value.Groups[1].Value);
        }
        Assert.Equal(target.UserName, fields["Details.Nickname"]);
        Assert.Equal(target.Email, fields["Details.Email"]);
        Assert.Equal("Anna Andersson", fields["Details.FullName"]);
        Assert.Equal("Storgatan 1", fields["Details.Address"]);
        Assert.Equal("123 45", fields["Details.PostalCode"]);
        Assert.Equal("Stockholm", fields["Details.City"]);
        Assert.Equal("+46701234567", fields["Details.PhoneNumber"]);
        fields["_handler"] = "edit-user-details";
        fields["Details.Email"] = "new-address@example.test";
        Assert.Equal(HttpStatusCode.Found, (await client.PostAsync(path, new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        using var check = factory.Services.CreateScope();
        var saved = (await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(target.Id))!;
        Assert.Equal("new-address@example.test", saved.Email);
        Assert.Equal(target.UserName, saved.UserName);
        Assert.Equal("Anna Andersson", saved.FullName);
        Assert.Equal("Storgatan 1", saved.Address);
        Assert.Equal("123 45", saved.PostalCode);
        Assert.Equal("Stockholm", saved.City);
        Assert.Equal("+46701234567", saved.PhoneNumber);
    }

    [Fact]
    public async Task Details_reject_non_admin_stale_invalid_and_duplicate_updates_without_partial_changes()
    {
        var admin = await CreateUser(AccessLevels.Admin);
        var user = await CreateUser();
        var principal = await Principal(admin);
        var ordinaryPrincipal = await Principal(user);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AdminUserService>();
        var input = new AdminUserInput { Version = user.ConcurrencyStamp!, Nickname = user.UserName!, Email = user.Email!, FullName = "Changed" };
        Assert.False((await service.UpdateDetailsAsync(ordinaryPrincipal, user.Id, input)).Succeeded);
        input.PostalCode = "123";
        Assert.False((await service.UpdateDetailsAsync(principal, user.Id, input)).Succeeded);
        input.PostalCode = null;
        input.Email = admin.Email!;
        Assert.False((await service.UpdateDetailsAsync(principal, user.Id, input)).Succeeded);
        input.Email = user.Email!;
        input.Nickname = admin.UserName!;
        Assert.False((await service.UpdateDetailsAsync(principal, user.Id, input)).Succeeded);
        using (var check = factory.Services.CreateScope())
            Assert.Null((await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(user.Id))!.FullName);
        input.Nickname = user.UserName!;
        Assert.True((await service.UpdateDetailsAsync(principal, user.Id, input)).Succeeded);
        Assert.False((await service.UpdateDetailsAsync(principal, user.Id, input)).Succeeded);
    }

    private async Task<ClaimsPrincipal> Principal(ApplicationUser user)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
    }

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task Login(HttpClient client, string email)
    {
        var response = await PostForm(client, "/login", "login", new()
        { ["Input.Email"] = email, ["Input.Password"] = Password });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostForm(HttpClient client, string path, string handler, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(path);
        fields["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken");
        fields["_handler"] = handler;
        if (html.Contains("name=\"Input.Version\"")) fields["Input.Version"] = Field(html, "Input.Version");
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private static string Field(string html, string name)
    {
        var match = Regex.Match(html, "name=\"" + Regex.Escape(name) + "\" value=\"([^\"]+)\"");
        Assert.True(match.Success, "Missing form field: " + name);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public void Dispose() => factory.Dispose();
}
