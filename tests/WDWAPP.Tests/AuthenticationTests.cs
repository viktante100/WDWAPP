using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WDWAPP.Data;
using WDWAPP.Security;
using Xunit;

namespace WDWAPP.Tests;

public sealed class AuthenticationTests : IClassFixture<AuthenticationFactory>
{
    private readonly AuthenticationFactory factory;
    public AuthenticationTests(AuthenticationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Registration_login_logout_and_cookie_nickname_work()
    {
        using var client = NewClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nickname = "SpelareÅ" + suffix;
        var email = suffix + "@example.test";
        const string password = "Test-password42!";
        var response = await PostForm(client, "/register", "register", new()
        {
            ["Input.Nickname"] = nickname, ["Input.Email"] = email,
            ["Input.Password"] = password, ["Input.ConfirmPassword"] = password,
            ["Input.Role"] = AccessLevels.Admin
        });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("WDWAPP.Identity="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.DoesNotContain("expires=", cookie.ToLowerInvariant());
        Assert.Contains(nickname, WebUtility.HtmlDecode(await client.GetStringAsync("/")));
        Assert.Contains("Inloggad", await client.GetStringAsync("/account"));

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.Empty(await users.GetRolesAsync(user));
            Assert.NotEqual(password, user.PasswordHash);
            Assert.Equal("Anna Andersson", user.FullName);
            Assert.Equal("Testgatan 12", user.Address);
            Assert.Equal("652 24", user.PostalCode);
            Assert.Equal("Karlstad", user.City);
        }

        var forgedLogout = await client.PostAsync("/account", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["_handler"] = "logout" }));
        Assert.Equal(HttpStatusCode.BadRequest, forgedLogout.StatusCode);
        Assert.Contains(nickname, WebUtility.HtmlDecode(await client.GetStringAsync("/")));

        response = await PostForm(client, "/account", "logout", new());
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("Ej inloggad", await client.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/account")).StatusCode);

        response = await PostForm(client, "/login", "login", new()
        {
            ["Input.Email"] = email.ToUpperInvariant(), ["Input.Password"] = password,
            ["Input.RememberMe"] = "true"
        });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("expires=", response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("WDWAPP.Identity=")).ToLowerInvariant());
        Assert.Contains(nickname, WebUtility.HtmlDecode(await client.GetStringAsync("/")));
    }

    [Fact]
    public async Task Authentication_posts_require_antiforgery()
    {
        using var client = NewClient();
        foreach (var path in new[] { "/login", "/register" })
        {
            var response = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Invalid_credentials_duplicates_and_external_return_urls_are_handled()
    {
        using var client = NewClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = suffix + "@example.test";
        var fields = new Dictionary<string, string>
        {
            ["Input.Nickname"] = "User" + suffix, ["Input.Email"] = email,
            ["Input.Password"] = "Test-password42!", ["Input.ConfirmPassword"] = "Test-password42!"
        };
        Assert.Equal(HttpStatusCode.Found, (await PostForm(client, "/register", "register", fields)).StatusCode);
        await PostForm(client, "/account", "logout", new());
        fields["Input.Nickname"] += "Other";
        var duplicate = await PostForm(client, "/register", "register", fields);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("already taken", await duplicate.Content.ReadAsStringAsync());

        var invalid = await PostForm(client, "/login", "login", new()
        { ["Input.Email"] = email, ["Input.Password"] = "wrong" });
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("Inloggningen misslyckades", await invalid.Content.ReadAsStringAsync());
        Assert.Contains("Ej inloggad", await client.GetStringAsync("/"));

        var valid = await PostForm(client, "/login?ReturnUrl=https%3A%2F%2Fexample.org", "login", new()
        { ["Input.Email"] = email, ["Input.Password"] = "Test-password42!" });
        Assert.Equal(HttpStatusCode.Found, valid.StatusCode);
        Assert.Equal(new Uri(client.BaseAddress!, "/"), valid.Headers.Location);
    }

    [Fact]
    public async Task Repeated_failed_logins_lock_the_account()
    {
        using var client = NewClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = suffix + "@example.test";
        await PostForm(client, "/register", "register", new()
        {
            ["Input.Nickname"] = "Locked" + suffix, ["Input.Email"] = email,
            ["Input.Password"] = "Test-password42!", ["Input.ConfirmPassword"] = "Test-password42!"
        });
        await PostForm(client, "/account", "logout", new());
        for (var attempt = 0; attempt < 5; attempt++)
            await PostForm(client, "/login", "login", new()
            { ["Input.Email"] = email, ["Input.Password"] = "wrong" });

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.True(await users.IsLockedOutAsync(user!));
        var response = await PostForm(client, "/login", "login", new()
        { ["Input.Email"] = email, ["Input.Password"] = "Test-password42!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Ej inloggad", await client.GetStringAsync("/"));
    }

    [Theory]
    [InlineData(null, false, false, false, false)]
    [InlineData("", true, false, false, false)]
    [InlineData(AccessLevels.Member, true, true, false, false)]
    [InlineData(AccessLevels.KeyMember, true, true, true, false)]
    [InlineData(AccessLevels.Admin, true, true, true, true)]
    public async Task Policies_enforce_cumulative_access(string? role, bool loggedIn, bool member, bool keyMember, bool admin)
    {
        using var scope = factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var identity = new ClaimsIdentity(role is null ? null : "Test");
        if (!string.IsNullOrEmpty(role)) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        var principal = new ClaimsPrincipal(identity);
        foreach (var (policy, expected) in new[]
        {
            (AccessLevels.LoggedIn, loggedIn), (AccessLevels.Member, member),
            (AccessLevels.KeyMember, keyMember), (AccessLevels.Admin, admin)
        })
            Assert.Equal(expected, (await authorization.AuthorizeAsync(principal, null, policy)).Succeeded);
    }

    [Theory]
    [InlineData("Input.FullName", " ")]
    [InlineData("Input.Address", "")]
    [InlineData("Input.PostalCode", "1234")]
    [InlineData("Input.PostalCode", "abcde")]
    [InlineData("Input.City", "")]
    public async Task Registration_rejects_missing_or_invalid_contact_details(string field, string value)
    {
        using var client = NewClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = suffix + "@example.test";
        var fields = new Dictionary<string, string>
        {
            ["Input.Nickname"] = "Contact" + suffix, ["Input.Email"] = email,
            ["Input.Password"] = "Test-password42!", ["Input.ConfirmPassword"] = "Test-password42!",
            [field] = value
        };
        var response = await PostForm(client, "/register", "register", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email));
    }

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> PostForm(HttpClient client, string path, string handler,
        Dictionary<string, string> fields)
    {
        if (handler == "register")
        {
            fields.TryAdd("Input.FullName", " Anna Andersson ");
            fields.TryAdd("Input.Address", " Testgatan 12 ");
            fields.TryAdd("Input.PostalCode", "65224");
            fields.TryAdd("Input.City", " Karlstad ");
        }
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token missing from " + path);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
        fields["_handler"] = handler;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
}

public sealed class AuthenticationFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), "wdwapp-tests-" + Guid.NewGuid() + ".db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath};Pooling=False",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(databasePath)) File.Delete(databasePath);
    }
}
