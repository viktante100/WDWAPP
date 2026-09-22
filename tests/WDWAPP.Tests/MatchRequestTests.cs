using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using WDWAPP.Data;
using WDWAPP.Security;
using WDWAPP.Services;
using Xunit;

namespace WDWAPP.Tests;

public sealed class MatchRequestTests : IDisposable
{
    private readonly AuthenticationFactory factory = new();
    private sealed record Actor(string Id, string Email, ClaimsPrincipal Principal, int? ProfileId, string Nickname);
    private async Task<Actor> User(string? role = AccessLevels.Member, bool profile = false, bool nickname = true)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var key = Guid.NewGuid().ToString("N");
        var user = new ApplicationUser { UserName = "Account" + key, Email = key + "@example.test", FullName = "Private Fullname" };
        Assert.True((await users.CreateAsync(user, "Test-password42!")).Succeeded);
        if (role is not null) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var player = profile ? new PlayerProfile { UserId = user.Id, Nickname = nickname ? "Nick" + key : null } : null;
        if (player is not null) { db.PlayerProfiles.Add(player); await db.SaveChangesAsync(); }
        return new(user.Id, user.Email, await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user), player?.Id, user.UserName!);
    }
    private static MatchRequestInput Input() => new() { Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10), Time = "18:30", Game = MatchGames.All[0] };
    private async Task<MatchRequestResult> Save(Actor actor, int? id = null, MatchRequestInput? input = null)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchRequestService>().SaveAsync(actor.Principal, id, input ?? Input());
    }
    private async Task<MatchRequestResult> Accept(Actor actor, MatchRequest item)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchRequestService>().AcceptAsync(actor.Principal, item.Id, item.Version);
    }
    private async Task<MatchRequestResult> Delete(Actor actor, MatchRequest item)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchRequestService>().DeleteAsync(actor.Principal, item.Id, item.Version, true);
    }
    private async Task<MatchRequest> Request(int id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MatchRequests.AsNoTracking()
            .Include(r => r.CalendarEvent).Include(r => r.OwnerUser).Include(r => r.AcceptedByUser).SingleAsync(r => r.Id == id);
    }
    private async Task<List<CalendarEvent>> Events()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Events.AsNoTracking().Summaries().ToListAsync();
    }
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
    private static Actor Guest => new("", "", new(new ClaimsIdentity()), null, "");

    [Fact]
    public async Task Guests_and_nonmembers_cannot_create_or_accept()
    {
        var owner = await User(AccessLevels.KeyMember);
        var item = await Request((await Save(owner)).Id!.Value);
        foreach (var actor in new[] { Guest, await User(null) })
        {
            Assert.False((await Save(actor)).Succeeded);
            Assert.False((await Accept(actor, item)).Succeeded);
        }
        Assert.Single(await Events());
    }

    [Theory]
    [InlineData(AccessLevels.Member)]
    [InlineData(AccessLevels.KeyMember)]
    [InlineData(AccessLevels.Admin)]
    public async Task Eligible_roles_create_one_public_calendar_entry_and_see_navigation(string role)
    {
        var owner = await User(role);
        var result = await Save(owner);
        Assert.True(result.Succeeded, result.Message);
        var item = await Request(result.Id!.Value);
        var entry = Assert.Single(await Events());
        Assert.Equal(CalendarEventType.MatchRequest, entry.Type);
        Assert.Equal(item.Id, entry.MatchRequestId);
        Assert.Equal(owner.Id, item.OwnerUserId);
        using var visitor = role == AccessLevels.Member ? await Login(await User(AccessLevels.KeyMember)) : Client();
        var html = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/kalender?Month={entry.Date:yyyy-MM}&Day={entry.Date:yyyy-MM-dd}"));
        Assert.Contains(owner.Nickname, html); Assert.Contains(item.Game, html); Assert.Contains("18:30", html);
        Assert.Contains("event-match", html); Assert.DoesNotContain("Private Fullname", html);
        Assert.Equal(role == AccessLevels.Member, (await visitor.GetStringAsync($"/sok-match/{item.Id}")).Contains("Acceptera match"));
        var redirect = await visitor.GetAsync($"/evenemang/{entry.Id}");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.EndsWith($"/sok-match/{item.Id}", redirect.Headers.Location!.OriginalString);
        using var client = await Login(owner);
        Assert.Contains("href=\"/sok-match\"", (await client.GetStringAsync("/")).Split("</nav>")[0]);
        Assert.Contains(owner.Nickname, await client.GetStringAsync("/sok-match"));
        var opponent = await User(AccessLevels.KeyMember);
        Assert.Null(owner.ProfileId);
        Assert.Null(opponent.ProfileId);
        Assert.True((await Accept(opponent, item)).Succeeded);
    }

    [Fact]
    public async Task Menu_and_routes_restrict_creation_but_details_remain_public()
    {
        using var guest = Client(); using var unpaid = await Login(await User(null));
        foreach (var client in new[] { guest, unpaid })
        {
            Assert.DoesNotContain("href=\"/sok-match\"", (await client.GetStringAsync("/")).Split("</nav>")[0]);
            Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/sok-match")).StatusCode);
            Assert.Equal(HttpStatusCode.Found, (await client.PostAsync("/sok-match", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        }
        using var noProfile = await Login(await User(profile: false));
        Assert.Contains("id=\"match-game\"", await noProfile.GetStringAsync("/sok-match"));
    }

    [Fact]
    public async Task Http_creation_and_acceptance_ignore_spoofed_identity_and_require_antiforgery()
    {
        var owner = await User(AccessLevels.KeyMember); var other = await User();
        using var client = await Login(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/sok-match", new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "match-request" }))).StatusCode);
        var input = Input();
        var response = await Post(client, "/sok-match", "match-request", new()
        {
            ["Input.Date"] = input.Date!.Value.ToString("yyyy-MM-dd"), ["Input.Time"] = input.Time, ["Input.Game"] = input.Game,
            ["Input.UserId"] = other.Id, ["Input.OwnerUserId"] = other.Id, ["Input.Nickname"] = "Forged"
        });
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var item = await Request(Assert.Single(await Events()).MatchRequestId!.Value);
        Assert.Equal(owner.Id, item.OwnerUserId);
        using var acceptor = await Login(other);
        var path = $"/sok-match/{item.Id}";
        Assert.Contains("Acceptera match", await acceptor.GetStringAsync(path));
        Assert.Equal(HttpStatusCode.BadRequest, (await acceptor.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "match-accept" }))).StatusCode);
        response = await Post(acceptor, path, "match-accept", new()
        { ["AcceptInput.Version"] = item.Version, ["AcceptInput.AcceptedByUserId"] = owner.Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(other.Id, (await Request(item.Id)).AcceptedByUserId);
    }

    [Fact]
    public async Task Only_one_other_player_can_accept_and_committed_acceptance_is_returned()
    {
        var owner = await User(AccessLevels.KeyMember); var other = await User(); var third = await User();
        var item = await Request((await Save(owner)).Id!.Value);
        Assert.False((await Accept(owner, item)).Succeeded);
        var result = await Accept(other, item);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(owner.Id, result.Acceptance!.OwnerUserId);
        Assert.Equal(other.Id, result.Acceptance.AcceptedByUserId);
        Assert.False((await Accept(third, item)).Succeeded);
        Assert.False((await Accept(other, await Request(item.Id))).Succeeded);
        var updated = await Request(item.Id);
        Assert.NotNull(updated.AcceptedUtc);
        var entry = Assert.Single(await Events());
        Assert.Equal(item.CalendarEvent.Id, entry.Id);
        Assert.Contains(owner.Nickname, entry.DisplayTitle); Assert.Contains(other.Nickname, entry.DisplayTitle);
        Assert.Contains("Bokad match", entry.DisplayTitle);
        Assert.False((await Delete(owner, updated)).Succeeded);
    }

    [Fact]
    public async Task Simultaneous_acceptance_in_separate_database_contexts_has_exactly_one_winner()
    {
        var owner = await User(AccessLevels.KeyMember); var one = await User(); var two = await User();
        var item = await Request((await Save(owner)).Id!.Value);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await start.Task; return await Accept(one, item); });
        var second = Task.Run(async () => { await start.Task; return await Accept(two, item); });
        start.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.Acceptance is not null);
        Assert.Contains((await Request(item.Id)).AcceptedByUserId, new[] { one.Id, two.Id });
        Assert.Single(await Events());
    }

    [Fact]
    public async Task Edits_and_deletes_enforce_ownership_versions_and_calendar_synchronization()
    {
        var owner = await User(AccessLevels.KeyMember); var other = await User(); var admin = await User(AccessLevels.Admin, profile: false);
        var item = await Request((await Save(owner)).Id!.Value);
        Assert.False((await Delete(other, item)).Succeeded);
        var edit = Input(); edit.Version = item.Version; edit.Date = edit.Date!.Value.AddDays(1); edit.Time = "20:00"; edit.Game = MatchGames.All[1];
        Assert.False((await Save(other, item.Id, edit)).Succeeded);
        Assert.True((await Save(owner, item.Id, edit)).Succeeded);
        var entry = Assert.Single(await Events());
        Assert.Equal(item.CalendarEvent.Id, entry.Id); Assert.Equal(edit.Date, entry.Date); Assert.Equal(new TimeOnly(20, 0), entry.Time);
        Assert.Contains(edit.Game, entry.DisplayTitle);
        Assert.False((await Accept(other, item)).Succeeded); // Must re-read changed date/game before accepting.
        Assert.False((await Delete(owner, item)).Succeeded);
        Assert.True((await Delete(owner, await Request(item.Id))).Succeeded);
        Assert.Empty(await Events());
        item = await Request((await Save(owner)).Id!.Value);
        Assert.True((await Accept(other, item)).Succeeded);
        edit.Version = (await Request(item.Id)).Version;
        Assert.False((await Save(owner, item.Id, edit)).Succeeded);
        Assert.True((await Delete(admin, await Request(item.Id))).Succeeded);
        Assert.Empty(await Events());
    }

    [Fact]
    public async Task Past_missing_invalid_games_and_invalid_times_are_rejected()
    {
        var owner = await User(AccessLevels.KeyMember);
        foreach (var input in new[] { new MatchRequestInput(), new() { Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), Time = "12:00", Game = MatchGames.All[0] },
            new() { Date = Input().Date, Time = "25:00", Game = MatchGames.All[0] }, new() { Date = Input().Date, Time = "18:00", Game = "Forged game" } })
            Assert.False((await Save(owner, input: input)).Succeeded);
        Assert.Empty(await Events());
    }

    [Fact]
    public async Task Revoked_membership_is_checked_from_database_not_stale_claims()
    {
        var owner = await User(AccessLevels.KeyMember); var other = await User();
        var item = await Request((await Save(owner)).Id!.Value);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.RemoveFromRoleAsync((await users.FindByIdAsync(other.Id))!, AccessLevels.Member)).Succeeded);
        }
        Assert.False((await Accept(other, item)).Succeeded);
        Assert.False((await Save(other)).Succeeded);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(CalendarEventType.MatchRequest, false)]
    [InlineData(CalendarEventType.LeagueRound, false)]
    [InlineData((CalendarEventType)999, false)]
    [InlineData(CalendarEventType.Tournament, true)]
    [InlineData(CalendarEventType.GameDay, true)]
    public async Task Manual_event_types_are_required_and_system_types_cannot_be_forged(CalendarEventType? type, bool succeeds)
    {
        var admin = await User(AccessLevels.Admin);
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<EventService>().SaveAsync(admin.Principal, null,
            new() { Type = type, Title = "Manual event", Description = "Test", Date = Input().Date });
        Assert.Equal(succeeds, result.Succeeded);
    }

    [Fact]
    public async Task Generated_match_events_cannot_be_edited_or_deleted_through_manual_event_service()
    {
        var owner = await User(AccessLevels.KeyMember); var admin = await User(AccessLevels.Admin);
        var item = await Request((await Save(owner)).Id!.Value);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await db.Events.AsNoTracking().SingleAsync();
        var service = scope.ServiceProvider.GetRequiredService<EventService>();
        Assert.False((await service.SaveAsync(admin.Principal, entry.Id, new() { Type = CalendarEventType.GameDay, Title = "Override", Description = "Override", Date = entry.Date, Version = entry.Version })).Succeeded);
        Assert.False((await service.DeleteAsync(admin.Principal, entry.Id, entry.Version, true)).Succeeded);
        db.ChangeTracker.Clear();
        db.Events.Add(new() { MatchRequestId = item.Id, Type = CalendarEventType.MatchRequest, Date = entry.Date });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Booked_matches_are_private_to_participants_with_admin_management_access()
    {
        var owner = await User(AccessLevels.KeyMember); var other = await User(); var unrelated = await User(AccessLevels.KeyMember); var admin = await User(AccessLevels.Admin);
        var item = await Request((await Save(owner)).Id!.Value);
        Assert.True((await Accept(other, item)).Succeeded);
        var calendar = $"/kalender?Month={item.CalendarEvent.Date:yyyy-MM}&Day={item.CalendarEvent.Date:yyyy-MM-dd}";
        using var guest = Client(); using var stranger = await Login(unrelated);
        foreach (var client in new[] { guest, stranger })
        {
            var html = await client.GetStringAsync(calendar);
            Assert.DoesNotContain(owner.Nickname, html);
            Assert.DoesNotContain($"href=\"/evenemang/{item.CalendarEvent.Id}\"", html);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/sok-match/{item.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/evenemang/{item.CalendarEvent.Id}")).StatusCode);
        }
        Assert.DoesNotContain(owner.Nickname, await stranger.GetStringAsync("/hantera/evenemang"));
        using var ownerClient = await Login(owner); using var otherClient = await Login(other);
        foreach (var client in new[] { ownerClient, otherClient })
        {
            var html = await client.GetStringAsync(calendar);
            Assert.Contains(owner.Nickname, html); Assert.Contains(other.Nickname, html);
            Assert.Contains("Bokad match", await client.GetStringAsync($"/sok-match/{item.Id}"));
        }
        using var adminClient = await Login(admin);
        Assert.DoesNotContain(owner.Nickname, await adminClient.GetStringAsync(calendar));
        Assert.Contains(owner.Nickname, await adminClient.GetStringAsync("/sok-match"));
        Assert.Contains(owner.Nickname, await adminClient.GetStringAsync("/hantera/evenemang"));
        Assert.Contains("Ta bort", await adminClient.GetStringAsync($"/sok-match/{item.Id}"));
    }

    [Fact]
    public async Task Multiple_events_on_one_date_are_listed_with_distinct_type_markers()
    {
        var owner = await User(AccessLevels.KeyMember); var item = await Request((await Save(owner)).Id!.Value);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Events.Add(new() { Title = "Same-day tournament", Type = CalendarEventType.Tournament, Date = item.CalendarEvent.Date });
            await db.SaveChangesAsync();
        }
        using var guest = Client();
        var html = WebUtility.HtmlDecode(await guest.GetStringAsync($"/kalender?Month={item.CalendarEvent.Date:yyyy-MM}&Day={item.CalendarEvent.Date:yyyy-MM-dd}"));
        Assert.Contains(owner.Nickname, html); Assert.Contains("Same-day tournament", html);
        Assert.Contains("calendar-dot event-match", html); Assert.Contains("calendar-dot event-tournament", html);
        Assert.Contains(", 2 evenemang", html);
    }

    [Fact]
    public async Task Deleting_a_player_profile_does_not_remove_or_change_account_bookings()
    {
        var owner = await User(AccessLevels.KeyMember, profile: true); var other = await User();
        var item = await Request((await Save(owner)).Id!.Value);
        Assert.True((await Accept(other, item)).Succeeded);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.PlayerProfiles.AsNoTracking().SingleAsync(p => p.Id == owner.ProfileId);
        Assert.True((await scope.ServiceProvider.GetRequiredService<PlayerProfileService>()
            .DeleteAsync(owner.Principal, profile.Id, profile.Version, true)).Succeeded);
        Assert.Equal(owner.Id, (await Request(item.Id)).OwnerUserId);
        Assert.Contains(owner.Nickname, Assert.Single(await Events()).DisplayTitle);
    }

    [Fact]
    public async Task Migration_preserves_existing_data_backfills_types_and_does_not_guess_from_titles()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260919135836_AddPlayerPortraitAndNamePrivacy");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO LeagueSeasons (Id, Year, Term, IsClosed, Version) VALUES (1, 2026, 2, 0, 's');
            INSERT INTO LeagueSessions (Id, SeasonId, Date, Status, Version) VALUES (1, 1, '2026-12-01', 0, 'r');
            INSERT INTO Events (Id, Title, Description, Date, Time, CreatedUtc, Version, LeagueSessionId) VALUES
                (1, 'Tournament-looking title', 'Keep manual', '2026-12-01', '18:30:00', '2026-09-19', 'm', NULL),
                (2, 'Round', 'Keep league', '2026-12-01', '17:00:00', '2026-09-19', 'l', 1);
            INSERT INTO NewsArticles (Title, Body, Links, PublishedUtc, Version, EventId) VALUES ('News', 'Keep news', '', '2026-09-19', 'n', 1);
            """);
        await migrator.MigrateAsync();
        var entries = await db.Events.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(CalendarEventType.GameDay, entries[0].Type);
        Assert.Equal(CalendarEventType.LeagueRound, entries[1].Type);
        Assert.Equal("Keep manual", entries[0].Description);
        Assert.Equal("Keep league", entries[1].Description);
        Assert.Equal(1, (await db.NewsArticles.SingleAsync()).EventId);
    }

    [Fact]
    public async Task Account_migration_preserves_open_and_accepted_requests_and_calendar_links()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260919141756_AddMatchRequestsAndEventTypes");
        db.Users.AddRange(new ApplicationUser { Id = "owner", UserName = "OwnerNick" }, new ApplicationUser { Id = "opponent", UserName = "OpponentNick" });
        await db.SaveChangesAsync();
        db.PlayerProfiles.AddRange(new PlayerProfile { Id = 1, UserId = "owner" }, new PlayerProfile { Id = 2, UserId = "opponent" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO MatchRequests (Id, OwnerPlayerProfileId, AcceptedByPlayerProfileId, Game, CreatedUtc, AcceptedUtc, Version) VALUES
                (1, 1, NULL, 'Kill Team', '2026-09-19', NULL, 'open'),
                (2, 1, 2, 'Blood Bowl', '2026-09-19', '2026-09-20', 'booked');
            INSERT INTO Events (Id, Title, Description, Date, Time, CreatedUtc, Version, Type, MatchRequestId) VALUES
                (1, '', '', '2027-12-01', '18:30:00', '2026-09-19', 'e1', 3, 1),
                (2, '', '', '2027-12-02', '19:00:00', '2026-09-19', 'e2', 3, 2);
            """);
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var requests = await db.MatchRequests.Include(r => r.CalendarEvent).OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal("owner", r.OwnerUserId));
        Assert.Null(requests[0].AcceptedByUserId);
        Assert.Equal("opponent", requests[1].AcceptedByUserId);
        Assert.Equal(new DateTime(2026, 9, 20), requests[1].AcceptedUtc);
        Assert.Equal("booked", requests[1].Version);
        Assert.Equal(2, requests[1].CalendarEvent.Id);
        Assert.Equal(new TimeOnly(19, 0), requests[1].CalendarEvent.Time);
        await db.PlayerProfiles.ExecuteDeleteAsync();
        Assert.Equal(2, await db.MatchRequests.CountAsync());
        Assert.Equal(2, await db.Events.CountAsync());
    }
    [Fact]
    public async Task Member_requests_are_visible_only_to_owner_and_key_members_and_forged_acceptance_is_denied()
    {
        var owner = await User(AccessLevels.Member); var member = await User(AccessLevels.Member);
        var key = await User(AccessLevels.KeyMember);
        var item = await Request((await Save(owner)).Id!.Value);
        var path = $"/sok-match/{item.Id}";
        var calendar = $"/kalender?Month={item.CalendarEvent.Date:yyyy-MM}&Day={item.CalendarEvent.Date:yyyy-MM-dd}";
        using var guest = Client(); using var memberClient = await Login(member);
        foreach (var client in new[] { guest, memberClient })
        {
            Assert.DoesNotContain(owner.Nickname, await client.GetStringAsync(calendar));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/evenemang/{item.CalendarEvent.Id}")).StatusCode);
        }
        Assert.False((await Accept(member, item)).Succeeded);
        // A valid antiforgery token from another page must not bypass match authorization.
        var form = await memberClient.GetStringAsync("/sok-match");
        var token = WebUtility.HtmlDecode(Regex.Match(form, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        var forged = await memberClient.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "match-accept", ["AcceptInput.Version"] = item.Version
        }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Null((await Request(item.Id)).AcceptedByUserId);
        using var ownerClient = await Login(owner); using var keyClient = await Login(key);
        Assert.Contains("Endast nyckelmedlemmar kommer att kunna se och besvara din efterlysning", WebUtility.HtmlDecode(await ownerClient.GetStringAsync("/sok-match")));
        Assert.Contains(">Boka match</a>", await ownerClient.GetStringAsync("/"));
        Assert.Contains(owner.Nickname, await ownerClient.GetStringAsync(path));
        Assert.Contains(owner.Nickname, await keyClient.GetStringAsync(calendar));
        Assert.Contains("Acceptera match", await keyClient.GetStringAsync(path));
        Assert.True((await Accept(key, item)).Succeeded);
    }

    [Fact]
    public async Task Key_membership_changes_apply_to_visibility_and_acceptance_without_relying_on_old_claims()
    {
        var owner = await User(AccessLevels.KeyMember); var member = await User(AccessLevels.Member);
        var item = await Request((await Save(owner)).Id!.Value);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(owner.Id))!;
        Assert.True((await users.RemoveFromRoleAsync(user, AccessLevels.KeyMember)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, AccessLevels.Member)).Succeeded);
        var service = scope.ServiceProvider.GetRequiredService<MatchRequestService>();
        Assert.False(await service.VisibleRequests(await service.AccessAsync(member.Principal)).AnyAsync(r => r.Id == item.Id));
        Assert.False((await Accept(member, item)).Succeeded);
        var otherRequest = await Request((await Save(member)).Id!.Value);
        Assert.False((await Accept(owner, otherRequest)).Succeeded);
        Assert.Null((await Request(item.Id)).AcceptedByUserId);
    }

    [Fact]
    public async Task Cleanup_deletes_open_and_booked_requests_after_stockholm_midnight_and_cascades_calendar_entries()
    {
        var owner = await User(AccessLevels.KeyMember); var member = await User();
        var open = await Request((await Save(owner)).Id!.Value);
        var booked = await Request((await Save(owner)).Id!.Value);
        Assert.True((await Accept(member, booked)).Succeeded);
        var future = await Request((await Save(owner)).Id!.Value);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var date = Input().Date!.Value;
        await db.Events.Where(e => e.MatchRequestId == future.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Date, date.AddDays(1)));
        db.Events.Add(new() { Title = "Keep manual event", Type = CalendarEventType.GameDay, Date = date.AddDays(-1) });
        await db.SaveChangesAsync();
        var midnight = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm").GetUtcOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        Assert.Equal(0, await MatchRequestCleanup.DeleteExpiredBatchAsync(db, midnight.AddSeconds(-1)));
        Assert.Equal(2, await MatchRequestCleanup.DeleteExpiredBatchAsync(db, midnight));
        Assert.Equal(future.Id, (await db.MatchRequests.SingleAsync()).Id);
        Assert.False(await db.Events.AnyAsync(e => e.MatchRequestId == open.Id || e.MatchRequestId == booked.Id));
        Assert.Equal(2, await db.Events.CountAsync());
        Assert.Equal(0, await MatchRequestCleanup.DeleteExpiredBatchAsync(db, midnight));
    }

    public void Dispose() => factory.Dispose();
}
