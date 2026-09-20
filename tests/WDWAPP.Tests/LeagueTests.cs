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

public sealed class LeagueTests : IDisposable
{
    private readonly AuthenticationFactory factory = new();
    private sealed record Player(string Id, int LeagueId, ClaimsPrincipal Principal, string Email);
    private async Task<Player> User(string? role = AccessLevels.Member, bool enroll = true)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var suffix = Guid.NewGuid().ToString("N");
        var user = new ApplicationUser { UserName = "P" + suffix[..10], Email = suffix + "@example.test" };
        Assert.True((await users.CreateAsync(user, "Test-password42!")).Succeeded);
        if (role is not null) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await db.LeagueSeasons.AnyAsync())
        {
            db.LeagueSeasons.Add(new() { Year = 2026, Term = LeagueTerm.Autumn });
            await db.SaveChangesAsync();
        }
        if (enroll)
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<LeagueService>().ExecuteAsync(principal, new() { Action = "enroll", SeasonId = (await db.LeagueSeasons.SingleAsync(s => !s.IsClosed)).Id })).Succeeded);
            return new(user.Id, (await db.LeaguePlayers.SingleAsync(p => p.UserId == user.Id)).Id, principal, user.Email);
        }
        return new(user.Id, 0, principal, user.Email);
    }
    private async Task<LeagueResult> Run(Player player, LeagueCommand command)
    {
        using var scope = factory.Services.CreateScope();
        if (command.Action == "enroll" && command.SeasonId == 0)
            command.SeasonId = (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LeagueSeasons.SingleAsync(s => !s.IsClosed)).Id;
        return await scope.ServiceProvider.GetRequiredService<LeagueService>().ExecuteAsync(player.Principal, command);
    }
    private async Task<List<T>> Rows<T>() where T : class
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Set<T>().AsNoTracking().ToListAsync();
    }
    private async Task<List<LeagueStanding>> Table(int? seasonId = null)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LeagueService>().StandingsAsync(seasonId);
    }
    private async Task<int> Round(Player admin, params Player[] players)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1 + (await Rows<LeagueSession>()).Count * 14);
        Assert.True((await Run(admin, new() { Action = "create", Date = date })).Succeeded);
        var id = (await Rows<LeagueSession>()).Max(s => s.Id);
        foreach (var player in players) Assert.True((await Run(player, new() { Action = "join", SessionId = id })).Succeeded);
        return id;
    }
    private async Task<LeagueResult> SessionAction(Player player, int id, string action)
        => await Run(player, new() { Action = action, SessionId = id, Version = (await Rows<LeagueSession>()).Single(s => s.Id == id).Version });
    private async Task<LeagueResult> MatchAction(Player player, int id, string action, LeagueOutcome outcome = LeagueOutcome.PlayerOneWin)
    {
        var match = (await Rows<LeagueMatch>()).Single(m => m.Id == id);
        return await Run(player, new() { Action = action, SessionId = match.SessionId, MatchId = id, Version = match.Version, Outcome = outcome,
            PlayerOneFaction = "Space Marines", PlayerTwoFaction = "Orks", PlayerOnePoints = outcome == LeagueOutcome.PlayerTwoWin ? 40 : 80, PlayerTwoPoints = outcome == LeagueOutcome.PlayerOneWin ? 40 : 80 });
    }
    private async Task<int> Report(Player one, Player two, int session, LeagueOutcome outcome = LeagueOutcome.PlayerOneWin)
    {
        Assert.True((await Run(one, new() { Action = "report", SessionId = session, PlayerId = one.LeagueId, OpponentId = two.LeagueId, Outcome = outcome,
            PlayerOneFaction = "Space Marines", PlayerTwoFaction = "Orks", PlayerOnePoints = outcome == LeagueOutcome.PlayerTwoWin ? 40 : 80, PlayerTwoPoints = outcome == LeagueOutcome.PlayerOneWin ? 40 : 80 })).Succeeded);
        return (await Rows<LeagueMatch>()).Single(m => m.SessionId == session && m.PlayerOneId == one.LeagueId).Id;
    }

    [Fact]
    public async Task Round_calendar_is_linked_synchronized_and_never_published_as_news()
    {
        var admin = await User(AccessLevels.Admin);
        var round = await Round(admin);
        var item = Assert.Single(await Rows<CalendarEvent>());
        Assert.Equal(round, item.LeagueSessionId);
        Assert.Equal(CalendarEventType.LeagueRound, item.Type);
        Assert.Equal($"40K-ligan – Omgång {round}", item.Title);
        Assert.Equal(LeagueService.CalendarDescription, item.Description);
        Assert.Equal(new TimeOnly(17, 0), item.Time);
        Assert.Equal((await Rows<LeagueSession>()).Single().Date, item.Date);
        Assert.Empty(await Rows<NewsArticle>());
        for (var i = 0; i < 2; i++)
        {
            var session = (await Rows<LeagueSession>()).Single();
            var date = session.Date.AddDays(1);
            Assert.True((await Run(admin, new() { Action = "date", SessionId = round, Date = date, Version = session.Version })).Succeeded);
            var updated = Assert.Single(await Rows<CalendarEvent>());
            Assert.Equal(item.Id, updated.Id);
            Assert.Equal(date, updated.Date);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<EventService>();
            var current = Assert.Single(await Rows<CalendarEvent>());
            Assert.False((await service.SaveAsync(admin.Principal, current.Id, new() { Type = CalendarEventType.Tournament, Title = "Override", Description = "Override", Date = current.Date, Version = current.Version, PublishAsNews = true })).Succeeded);
            Assert.False((await service.DeleteAsync(admin.Principal, current.Id, current.Version, true)).Succeeded);
        }
        Assert.True((await SessionAction(admin, round, "cancel")).Succeeded);
        Assert.Empty(await Rows<CalendarEvent>());
        Assert.True((await SessionAction(admin, round, "reopen")).Succeeded);
        Assert.Single(await Rows<CalendarEvent>());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.LeagueSessions.Where(s => s.Id == round).ExecuteDeleteAsync();
        }
        Assert.Empty(await Rows<CalendarEvent>());
        await Round(admin);
        var season = (await Rows<LeagueSeason>()).Single();
        Assert.True((await Run(admin, new() { Action = "delete-season", SeasonId = season.Id, Version = season.Version, Confirmed = true })).Succeeded);
        Assert.Empty(await Rows<CalendarEvent>());
        Assert.Empty(await Rows<NewsArticle>());
    }

    public static TheoryData<int, int, int, int> Brackets => new()
    {
        {0,10,10,0}, {25,10,10,0}, {26,9,11,1}, {50,9,11,1}, {51,8,12,2}, {75,8,12,2},
        {76,7,13,3}, {100,7,13,3}, {101,6,15,4}, {125,6,15,4}, {126,6,16,5}, {150,6,16,5},
        {151,5,17,6}, {200,5,17,6}, {201,4,18,7}, {250,4,18,7}, {251,3,19,8}, {300,3,19,8},
        {301,2,20,9}, {400,2,20,9}, {401,2,30,10}, {500,2,30,10}, {501,2,40,15}, {2000,2,40,15}
    };
    [Theory, MemberData(nameof(Brackets))]
    public void Ranking_tables_cover_every_boundary_in_both_directions(int difference, int favorite, int underdog, int draw)
    {
        Assert.Equal(favorite, LeagueRanking.Delta(500 + difference, 500, LeagueOutcome.PlayerOneWin));
        Assert.Equal(underdog, LeagueRanking.Delta(500, 500 + difference, LeagueOutcome.PlayerOneWin));
        Assert.Equal(-favorite, LeagueRanking.Delta(500, 500 + difference, LeagueOutcome.PlayerTwoWin));
        Assert.Equal(-underdog, LeagueRanking.Delta(500 + difference, 500, LeagueOutcome.PlayerTwoWin));
        Assert.Equal(-draw, LeagueRanking.Delta(500 + difference, 500, LeagueOutcome.Draw));
        Assert.Equal(draw, LeagueRanking.Delta(500, 500 + difference, LeagueOutcome.Draw));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(AccessLevels.Member, true)]
    [InlineData(AccessLevels.KeyMember, true)]
    [InlineData(AccessLevels.Admin, true)]
    public async Task Roles_are_checked_at_the_service_and_http_boundaries(string? role, bool allowed)
    {
        var player = await User(role, false);
        Assert.Equal(allowed, (await Run(player, new() { Action = "enroll" })).Succeeded);
        Assert.Equal(role == AccessLevels.Admin, (await Run(player, new() { Action = "create", Date = new(2026, 1, 1) })).Succeeded);
        using var client = await Login(player);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/ligan/spela")).StatusCode);
        Assert.Equal(role == AccessLevels.Admin ? HttpStatusCode.OK : HttpStatusCode.Redirect, (await client.GetAsync("/hantera/ligan")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ligan")).StatusCode);
    }

    [Fact]
    public async Task Report_dispute_resubmit_confirm_enforces_ownership_and_locking()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User(AccessLevels.KeyMember); var stranger = await User();
        var round = await Round(admin, a, b, stranger);
        Assert.False((await Run(a, new() { Action = "report", SessionId = round, OpponentId = a.LeagueId })).Succeeded);
        var match = await Report(a, b, round);
        Assert.False((await Run(stranger, new() { Action = "report", SessionId = round, OpponentId = a.LeagueId })).Succeeded);
        Assert.False((await Run(admin, new() { Action = "bye", SessionId = round, PlayerId = a.LeagueId })).Succeeded);
        Assert.All(await Table(), r => { Assert.Equal(0, r.Points); Assert.Equal(500, r.ELO); });
        Assert.False((await MatchAction(a, match, "confirm")).Succeeded);
        Assert.False((await MatchAction(stranger, match, "confirm")).Succeeded);
        Assert.False((await MatchAction(stranger, match, "edit")).Succeeded);
        Assert.False((await SessionAction(admin, round, "finalize")).Succeeded);
        Assert.True((await MatchAction(b, match, "dispute")).Succeeded);
        Assert.Equal(LeagueMatchStatus.Disputed, (await Rows<LeagueMatch>()).Single().Status);
        Assert.All(await Table(), r => Assert.Equal(0, r.Matches));
        Assert.False((await MatchAction(b, match, "confirm")).Succeeded);
        Assert.True((await MatchAction(b, match, "edit", LeagueOutcome.Draw)).Succeeded);
        Assert.False((await MatchAction(b, match, "confirm")).Succeeded);
        Assert.True((await MatchAction(a, match, "confirm")).Succeeded);
        Assert.False((await MatchAction(a, match, "edit")).Succeeded);
        Assert.False((await MatchAction(b, match, "edit")).Succeeded);
        Assert.False((await SessionAction(b, round, "finalize")).Succeeded);
        var table = await Table();
        foreach (var row in table.Where(r => r.PlayerId == a.LeagueId || r.PlayerId == b.LeagueId))
        { Assert.Equal(2, row.Points); Assert.Equal(1, row.D); Assert.Equal(2m, row.PPM); Assert.Equal(500, row.ELO); }
        Assert.Empty(await Rows<LeagueRating>());
        Assert.True((await SessionAction(admin, round, "finalize")).Succeeded);
        Assert.False((await Run(stranger, new() { Action = "report", SessionId = round, OpponentId = a.LeagueId })).Succeeded);
        Assert.True((await MatchAction(admin, match, "edit", LeagueOutcome.PlayerTwoWin)).Succeeded);
        table = await Table();
        Assert.Equal(1, table.Single(r => r.PlayerId == a.LeagueId).L);
        Assert.Equal(1, table.Single(r => r.PlayerId == a.LeagueId).Points);
        Assert.Equal(3, table.Single(r => r.PlayerId == b.LeagueId).Points);
        Assert.Equal(510, table.Single(r => r.PlayerId == b.LeagueId).ELO);
        Assert.True((await MatchAction(admin, match, "delete")).Succeeded);
        Assert.Empty(await Rows<LeagueMatch>()); Assert.Empty(await Rows<LeagueRating>());
        Assert.All(await Table(), r => Assert.Equal(0, r.Points));
    }

    [Fact]
    public async Task Byes_are_excluded_from_played_statistics_and_ppm_and_positions_survive_sorting()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User(); var byeOnly = await User();
        foreach (var outcome in new[] { LeagueOutcome.PlayerOneWin, LeagueOutcome.PlayerOneWin, LeagueOutcome.Draw, LeagueOutcome.PlayerTwoWin })
        {
            var round = await Round(admin, a, b);
            var match = await Report(a, b, round, outcome);
            Assert.True((await MatchAction(b, match, "confirm")).Succeeded);
        }
        foreach (var recipient in new[] { a, a, byeOnly, byeOnly })
        {
            var round = await Round(admin, a, b, byeOnly);
            Assert.True((await Run(admin, new() { Action = "bye", SessionId = round, PlayerId = recipient.LeagueId })).Succeeded);
            Assert.False((await Run(recipient, new() { Action = "report", SessionId = round, OpponentId = b.LeagueId })).Succeeded);
            Assert.True((await SessionAction(admin, round, "finalize")).Succeeded);
        }
        var table = await Table(); var row = table.Single(r => r.PlayerId == a.LeagueId);
        Assert.Equal((4, 2, 1, 1, 2.25m, 15, 500), (row.Matches, row.W, row.D, row.L, row.PPM, row.Points, row.ELO));
        var bye = table.Single(r => r.PlayerId == byeOnly.LeagueId);
        Assert.Equal((0, 0, 0, 0, 0m, 6, 500), (bye.Matches, bye.W, bye.D, bye.L, bye.PPM, bye.Points, bye.ELO));
        Assert.Empty(await Rows<LeagueRating>());
        Assert.Equal(a.LeagueId, table[0].PlayerId);
        foreach (var column in new[] { "Player", "Matches", "W", "D", "L", "PPM", "ELO", "Points" })
            foreach (var sorted in LeagueRanking.Sort(table, column, false)) Assert.Equal(table.Single(r => r.PlayerId == sorted.PlayerId).Position, sorted.Position);
    }

    [Fact]
    public async Task Historical_corrections_replay_later_ratings_and_cancellation_removes_results()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User();
        var rounds = new List<int>(); var matches = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var round = await Round(admin, a, b); rounds.Add(round);
            var match = await Report(a, b, round); matches.Add(match);
            Assert.True((await MatchAction(b, match, "confirm")).Succeeded);
            Assert.Equal(i * 2, (await Rows<LeagueRating>()).Count);
            Assert.True((await SessionAction(admin, round, "finalize")).Succeeded);
        }
        Assert.Equal(537, (await Table()).Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.True((await MatchAction(admin, matches[0], "edit", LeagueOutcome.PlayerTwoWin)).Succeeded);
        Assert.Equal(520, (await Table()).Single(r => r.PlayerId == a.LeagueId).ELO);
        var ledger = await Rows<LeagueRating>();
        Assert.Equal(490, ledger.Single(r => r.MatchId == matches[1] && r.PlayerId == a.LeagueId).Before);
        Assert.Equal(8, ledger.Count);
        Assert.All(ledger.GroupBy(r => r.MatchId), group => Assert.Equal(0, group.Sum(r => r.Change)));
        Assert.True((await SessionAction(admin, rounds[0], "cancel")).Succeeded);
        Assert.Equal(3, (await Table()).Single(r => r.PlayerId == a.LeagueId).Matches);
        Assert.Equal(529, (await Table()).Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.True((await SessionAction(admin, rounds[1], "reopen")).Succeeded);
        Assert.Equal(520, (await Table()).Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.NotEmpty(await Rows<LeagueAudit>());
    }

    [Fact]
    public async Task Revoked_roles_stale_versions_and_forged_claims_cannot_mutate_league()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User();
        var round = await Round(admin, a, b); var match = await Report(a, b, round);
        var previous = (await Rows<LeagueMatch>()).Single();
        Assert.True((await MatchAction(b, match, "confirm")).Succeeded);
        Assert.False((await Run(admin, new() { Action = "edit", SessionId = round, MatchId = match, Version = previous.Version })).Succeeded);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(a.Id))!;
        await users.RemoveFromRoleAsync(user, AccessLevels.Member);
        Assert.False((await Run(a, new() { Action = "join", SessionId = round })).Succeeded);
        var forged = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, AccessLevels.Admin) }, "forged"));
        Assert.False((await scope.ServiceProvider.GetRequiredService<LeagueService>().ExecuteAsync(forged, new() { Action = "create", Date = new(2026, 1, 1) })).Succeeded);
    }

    [Fact]
    public async Task Html_forms_bind_commands_require_antiforgery_and_hide_admin_controls()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User();
        using var client = await Login(a); using var adminClient = await Login(admin);
        using var visitor = NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/ligan/spela")).StatusCode);
        Assert.Contains("href=\"/ligan\"", await visitor.GetStringAsync("/"));
        Assert.DoesNotContain("/hantera/ligan", await visitor.GetStringAsync("/ligan"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/ligan/atgard?action=enroll", new FormUrlEncodedContent(new Dictionary<string,string> { ["_handler"] = "league-action", ["Input.Action"] = "enroll" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Found, (await Post(adminClient, "/ligan/atgard?action=create", "league-action", new() { ["Input.Action"] = "create", ["Input.Date"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd") })).StatusCode);
        var round = (await Rows<LeagueSession>()).Single().Id;
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/ligan/atgard?action=join&session={round}", "league-action", new() { ["Input.Action"] = "join", ["Input.SessionId"] = round.ToString() })).StatusCode);
        Assert.True((await Run(b, new() { Action = "join", SessionId = round })).Succeeded);
        Assert.Equal(HttpStatusCode.Found, (await Post(client, $"/ligan/atgard?action=report&session={round}", "league-action", new() { ["Input.Action"] = "report", ["Input.SessionId"] = round.ToString(), ["Input.OpponentId"] = b.LeagueId.ToString(), ["Input.PlayerOneFaction"] = "Space Marines", ["Input.PlayerTwoFaction"] = "Orks", ["Input.PlayerOnePoints"] = "80", ["Input.PlayerTwoPoints"] = "40" })).StatusCode);
        Assert.Single(await Rows<LeagueMatch>());
        var html = await client.GetStringAsync($"/ligan/omgang/{round}");
        Assert.Contains(">PTSF</th>", html);
        Assert.Contains(">PTSA</th>", html);
        Assert.DoesNotContain("action=finalize", html);
        Assert.DoesNotContain("action=confirm", html);
        Assert.Contains("action=finalize", await adminClient.GetStringAsync($"/ligan/omgang/{round}"));
        var publicTable = await visitor.GetStringAsync("/ligan");
        Assert.Contains("title=\"Position\">P</a>", publicTable);
        Assert.Equal(new[] { "1", "2", "3" }, Regex.Matches(publicTable, "<tr><td>([0-9]+)</td>").Select(m => m.Groups[1].Value));
        var reversedTable = await visitor.GetStringAsync("/ligan?Sort=Position&Ascending=false");
        Assert.Equal(new[] { "3", "2", "1" }, Regex.Matches(reversedTable, "<tr><td>([0-9]+)</td>").Select(m => m.Groups[1].Value));
        if (Environment.GetEnvironmentVariable("WDW_VISUAL_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "league.html"), await client.GetStringAsync("/ligan"));
            await File.WriteAllTextAsync(Path.Combine(output, "round.html"), html);
        }
    }
    [Fact]
    public async Task Midseason_join_has_no_retroactive_statistics_and_preserves_existing_players()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User();
        // Existing players joined before an earlier round which remained open.
        var oldDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.LeaguePlayers.ExecuteUpdateAsync(set => set.SetProperty(p => p.JoinedUtc, DateTime.UtcNow.AddDays(-60)));
            await db.LeagueRegistrations.ExecuteUpdateAsync(set => set.SetProperty(p => p.JoinedUtc, DateTime.UtcNow.AddDays(-60)));
        }
        Assert.True((await Run(admin, new() { Action = "create", Date = oldDate })).Succeeded);
        var earlier = (await Rows<LeagueSession>()).Single().Id;
        foreach (var player in new[] { a, b }) Assert.True((await Run(player, new() { Action = "join", SessionId = earlier })).Succeeded);
        var match = await Report(a, b, earlier);
        Assert.True((await MatchAction(b, match, "confirm")).Succeeded);
        Assert.True((await SessionAction(admin, earlier, "finalize")).Succeeded);
        var before = await Table();
        var late = await User();
        var after = await Table();
        var newcomer = after.Single(r => r.PlayerId == late.LeagueId);
        Assert.Equal((500, 0, 0, 0, 0, 0, 0m), (newcomer.ELO, newcomer.Points, newcomer.Matches, newcomer.W, newcomer.D, newcomer.L, newcomer.PPM));
        foreach (var original in before)
        {
            var current = after.Single(r => r.PlayerId == original.PlayerId);
            Assert.Equal(original with { Position = 0 }, current with { Position = 0 });
        }
        Assert.True((await SessionAction(admin, earlier, "reopen")).Succeeded);
        Assert.False((await Run(late, new() { Action = "join", SessionId = earlier })).Succeeded);
        Assert.False((await Run(admin, new() { Action = "bye", SessionId = earlier, PlayerId = late.LeagueId })).Succeeded);
        Assert.DoesNotContain(await Rows<LeagueParticipation>(), p => p.PlayerId == late.LeagueId);
        var future = await Round(admin, a, late);
        var newMatch = await Report(late, a, future);
        Assert.True((await MatchAction(a, newMatch, "confirm")).Succeeded);
        Assert.Equal(3, (await Table()).Single(r => r.PlayerId == late.LeagueId).Points);
        Assert.Equal(1, (await Table()).Single(r => r.PlayerId == late.LeagueId).Matches);
        // Re-enrolling never resets existing statistics or the original join timestamp.
        var joined = (await Rows<LeaguePlayer>()).Single(p => p.Id == late.LeagueId).JoinedUtc;
        Assert.True((await Run(late, new() { Action = "enroll" })).Succeeded);
        Assert.Equal(joined, (await Rows<LeaguePlayer>()).Single(p => p.Id == late.LeagueId).JoinedUtc);
        Assert.Equal(3, (await Table()).Single(r => r.PlayerId == late.LeagueId).Points);
    }

    [Fact]
    public async Task Database_rejects_duplicate_participation_cross_slot_matches_self_play_and_bye_overlap()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User(); var c = await User();
        var round = await Round(admin, a, b, c); await Report(a, b, round);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LeagueParticipations.Add(new() { SessionId = round, PlayerId = a.LeagueId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        db.LeagueMatches.Add(new() { SessionId = round, PlayerOneId = c.LeagueId, PlayerTwoId = a.LeagueId, ReporterId = c.LeagueId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        db.LeagueMatches.Add(new() { SessionId = round, PlayerOneId = c.LeagueId, PlayerTwoId = c.LeagueId, ReporterId = c.LeagueId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        var entry = await db.LeagueParticipations.SingleAsync(p => p.SessionId == round && p.PlayerId == a.LeagueId);
        entry.IsBye = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Equal_points_use_elo_tiebreak_and_admin_players_cannot_self_confirm()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User(); var c = await User();
        var first = await Round(admin, admin, a);
        var own = await Report(admin, a, first);
        Assert.Equal(LeagueMatchStatus.Pending, (await Rows<LeagueMatch>()).Single().Status);
        Assert.False((await MatchAction(admin, own, "confirm")).Succeeded);
        Assert.True((await MatchAction(a, own, "confirm")).Succeeded);
        Assert.True((await SessionAction(admin, first, "finalize")).Succeeded);
        var second = await Round(admin, b, c);
        var other = await Report(b, c, second);
        Assert.False((await MatchAction(admin, other, "confirm")).Succeeded);
        Assert.True((await MatchAction(c, other, "confirm")).Succeeded);
        var rows = await Table();
        Assert.Equal(3, rows.Single(r => r.PlayerId == admin.LeagueId).Points);
        Assert.Equal(3, rows.Single(r => r.PlayerId == b.LeagueId).Points);
        Assert.True(rows.Single(r => r.PlayerId == admin.LeagueId).Position < rows.Single(r => r.PlayerId == b.LeagueId).Position);
    }

    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    [Fact]
    public async Task Inline_confirmation_and_dispute_are_single_posts_with_ownership_csrf_and_stale_edit_protection()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User(); var outsider = await User();
        var first = await Round(admin, a, b); var second = await Round(admin, a, b);
        var firstMatch = await Report(a, b, first); var secondMatch = await Report(a, b, second);
        using var opponent = await Login(b); using var reporter = await Login(a); using var other = await Login(outsider);
        var pending = (await Rows<LeagueMatch>()).Single(m => m.Id == firstMatch);
        var fields = new Dictionary<string, string> { ["Input.Action"] = "confirm", ["Input.MatchId"] = firstMatch.ToString(), ["Input.SessionId"] = first.ToString(), ["Input.Version"] = pending.Version };
        var html = WebUtility.HtmlDecode(await opponent.GetStringAsync("/ligan"));
        Assert.Contains($"league-decision-{firstMatch}", html); Assert.Contains($"league-decision-{secondMatch}", html);
        Assert.DoesNotContain("Väntar på motståndaren", html);
        Assert.DoesNotContain("action=confirm", html);
        var noCsrf = new Dictionary<string, string>(fields) { ["_handler"] = $"league-decision-{firstMatch}" };
        Assert.Equal(HttpStatusCode.BadRequest, (await opponent.PostAsync("/ligan", new FormUrlEncodedContent(noCsrf))).StatusCode);
        await Post(reporter, "/ligan", $"league-decision-{firstMatch}", new(fields));
        await Post(other, "/ligan", $"league-decision-{firstMatch}", new(fields));
        Assert.All(await Rows<LeagueMatch>(), m => Assert.Equal(LeagueMatchStatus.Pending, m.Status));
        var stale = new Dictionary<string, string>(fields) { ["Input.Version"] = "outdated" };
        Assert.Equal(HttpStatusCode.OK, (await Post(opponent, "/ligan", $"league-decision-{firstMatch}", stale)).StatusCode);
        Assert.All(await Rows<LeagueMatch>(), m => Assert.Equal(LeagueMatchStatus.Pending, m.Status));
        var response = await Post(opponent, "/ligan", $"league-decision-{firstMatch}", fields);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/ligan", new Uri(new Uri("https://localhost"), response.Headers.Location!).AbsolutePath);
        Assert.Equal(LeagueMatchStatus.Confirmed, (await Rows<LeagueMatch>()).Single(m => m.Id == firstMatch).Status);
        Assert.Equal(LeagueMatchStatus.Pending, (await Rows<LeagueMatch>()).Single(m => m.Id == secondMatch).Status);
        var remaining = (await Rows<LeagueMatch>()).Single(m => m.Id == secondMatch);
        Assert.Equal(HttpStatusCode.Found, (await Post(opponent, "/ligan", $"league-decision-{secondMatch}", new()
        { ["Input.Action"] = "dispute", ["Input.MatchId"] = secondMatch.ToString(), ["Input.SessionId"] = second.ToString(), ["Input.Version"] = remaining.Version })).StatusCode);
        html = WebUtility.HtmlDecode(await opponent.GetStringAsync("/ligan"));
        Assert.DoesNotContain("Bekräftad", html); Assert.DoesNotContain("Väntar på motståndaren", html);
        Assert.Contains("Bestridd", html);
        Assert.Equal(1, (await Table()).Single(r => r.PlayerId == a.LeagueId).Matches);
    }

    [Fact]
    public async Task Report_form_excludes_self_and_unnecessary_copy_and_server_rejects_forged_self_play()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User();
        var round = await Round(admin, a, b);
        using var client = await Login(a);
        var path = $"/ligan/atgard?action=report&session={round}";
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(path));
        var options = Regex.Match(html, "<select[^>]*id=\"opponent\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(options);
        Assert.DoesNotContain($"value=\"{a.LeagueId}\"", options);
        Assert.Contains($"value=\"{b.LeagueId}\"", options);
        Assert.DoesNotContain("<h1>Rapportera match</h1>", html);
        Assert.DoesNotContain("beräknas från matchpoängen", html);
        Assert.DoesNotContain("Motståndaren måste bekräfta", html);
        var response = await Post(client, path, "league-action", new()
        { ["Input.Action"] = "report", ["Input.SessionId"] = round.ToString(), ["Input.OpponentId"] = a.LeagueId.ToString(),
            ["Input.PlayerOneFaction"] = "Orks", ["Input.PlayerTwoFaction"] = "Necrons", ["Input.PlayerOnePoints"] = "90", ["Input.PlayerTwoPoints"] = "50" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Rows<LeagueMatch>());
    }

    [Fact]
    public async Task Ppg_is_played_league_points_while_ptsf_and_ptsa_are_actual_scores_without_byes_or_unconfirmed_games()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User();
        for (var i = 0; i < 3; i++)
        {
            var round = await Round(admin, a, b); var match = await Report(a, b, round);
            Assert.True((await MatchAction(b, match, "confirm")).Succeeded);
        }
        var byeRound = await Round(admin, a, b, admin);
        Assert.True((await Run(admin, new() { Action = "bye", SessionId = byeRound, PlayerId = a.LeagueId })).Succeeded);
        var pendingRound = await Round(admin, a, b); await Report(a, b, pendingRound);
        var disputedRound = await Round(admin, a, b); var disputed = await Report(a, b, disputedRound);
        Assert.True((await MatchAction(b, disputed, "dispute")).Succeeded);
        var rows = await Table(); var row = rows.Single(r => r.PlayerId == a.LeagueId);
        Assert.Equal((3m, 12, 3, 240, 120), (row.PPG, row.Points, row.Matches, row.PTSF, row.PTSA));
        var loser = rows.Single(r => r.PlayerId == b.LeagueId);
        Assert.Equal((1m, 120, 240), (loser.PPG, loser.PTSF, loser.PTSA));
        var zero = rows.Single(r => r.PlayerId == admin.LeagueId);
        Assert.Equal((0m, 0, 0), (zero.PPG, zero.PTSF, zero.PTSA));
        foreach (var column in new[] { "PPG", "PTSF", "PTSA" })
            foreach (var sorted in LeagueRanking.Sort(rows, column, false))
                Assert.Equal(rows.Single(r => r.PlayerId == sorted.PlayerId).Position, sorted.Position);
    }
    [Fact]
    public async Task Match_details_are_validated_and_personal_matches_appear_directly_below_standings()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User(); var stranger = await User();
        var round = await Round(admin, a, b, stranger);
        var command = new LeagueCommand { Action = "report", SessionId = round, OpponentId = b.LeagueId,
            PlayerOneFaction = "Aeldari", PlayerTwoFaction = "Necrons", PlayerOnePoints = 92, PlayerTwoPoints = 61,
            Outcome = LeagueOutcome.PlayerTwoWin }; // The submitted outcome cannot override the score.
        command.PlayerOneFaction = " "; Assert.False((await Run(a, command)).Succeeded);
        command.PlayerOneFaction = "Aeldari"; command.PlayerTwoPoints = -1;
        Assert.False((await Run(a, command)).Succeeded);
        command.PlayerTwoPoints = null; Assert.False((await Run(a, command)).Succeeded);
        command.PlayerTwoPoints = 61;
        Assert.True((await Run(a, command)).Succeeded);
        var match = Assert.Single(await Rows<LeagueMatch>());
        Assert.Equal(LeagueOutcome.PlayerOneWin, match.Outcome);
        Assert.Equal(("Aeldari", "Necrons", 92, 61), (match.PlayerOneFaction, match.PlayerTwoFaction, match.PlayerOnePoints, match.PlayerTwoPoints));
        using var client = await Login(a); using var opponent = await Login(b); using var outsider = await Login(stranger); using var visitor = NewClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/ligan"));
        Assert.Contains("<h2>Mina matcher</h2>", html);
        Assert.True(html.IndexOf("</table>", StringComparison.Ordinal) < html.IndexOf("<h2>Mina matcher", StringComparison.Ordinal));
        Assert.Contains("Aeldari", html); Assert.Contains("Necrons", html);
        Assert.Contains("<td>92</td><td>61</td><td>W</td>", html);
        Assert.Contains("<td>61</td><td>92</td><td>L</td>", html);
        if (Environment.GetEnvironmentVariable("WDW_LEAGUE_LAYOUT_OUTPUT") is { Length: > 0 } layoutPath)
        {
            var root = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().ContentRootPath;
            var fixture = Regex.Replace(html, "<script\\b[^>]*>.*?</script>|<link\\b[^>]*>|<base\\b[^>]*>", "", RegexOptions.Singleline);
            await File.WriteAllTextAsync(layoutPath, fixture.Replace("</head>", "<style>" + await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css")) + "</style></head>"));
        }
        var users = await Rows<ApplicationUser>();
        Assert.Contains(users.Single(u => u.Id == a.Id).UserName!, html);
        Assert.Contains(users.Single(u => u.Id == b.Id).UserName!, html);
        Assert.DoesNotContain("href=\"/ligan/spela\"", html);
        Assert.DoesNotContain("SeasonId=", html);
        Assert.DoesNotContain("action=confirm", html);
        Assert.Contains($"league-decision-{match.Id}", await opponent.GetStringAsync("/ligan"));
        Assert.DoesNotContain("Aeldari", await outsider.GetStringAsync("/ligan"));
        Assert.DoesNotContain("Mina matcher", await visitor.GetStringAsync("/ligan"));
        var confirmPage = await opponent.GetStringAsync($"/ligan/atgard?action=confirm&session={round}&match={match.Id}");
        Assert.Contains("Aeldari", confirmPage); Assert.Contains("Necrons", confirmPage);
        Assert.True((await MatchAction(b, match.Id, "confirm")).Succeeded);
        Assert.True((await MatchAction(admin, match.Id, "edit", LeagueOutcome.Draw)).Succeeded);
        html = await client.GetStringAsync("/ligan");
        Assert.Equal(2, Regex.Matches(html, "<td>D</td>").Count);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.LeagueMatches.Where(m => m.Id == match.Id).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.PlayerOneFaction, (string?)null).SetProperty(m => m.PlayerTwoFaction, (string?)null)
                .SetProperty(m => m.PlayerOnePoints, (int?)null).SetProperty(m => m.PlayerTwoPoints, (int?)null));
        }
        Assert.Contains("<td>–</td>", WebUtility.HtmlDecode(await client.GetStringAsync("/ligan")));
    }
    [Fact]
    public async Task Returning_player_must_explicitly_register_for_each_season_before_appearing_or_joining_rounds()
    {
        var admin = await User(AccessLevels.Admin); var a = await User();
        var old = (await Rows<LeagueSeason>()).Single();
        Assert.True((await Run(admin, new() { Action = "close-season", SeasonId = old.Id, Version = old.Version })).Succeeded);
        Assert.True((await Run(admin, new() { Action = "create-season", Year = 2027, Term = LeagueTerm.Spring })).Succeeded);
        var next = (await Rows<LeagueSeason>()).Single(s => !s.IsClosed);
        var round = await Round(admin);
        Assert.Empty(await Table());
        Assert.False((await Run(a, new() { Action = "join", SessionId = round })).Succeeded);
        Assert.False((await Run(a, new() { Action = "enroll", SeasonId = old.Id })).Succeeded);
        Assert.False((await Run(a, new() { Action = "enroll", SeasonId = int.MaxValue })).Succeeded);
        using (var scope = factory.Services.CreateScope())
            Assert.False((await scope.ServiceProvider.GetRequiredService<LeagueService>().ExecuteAsync(a.Principal, new() { Action = "enroll" })).Succeeded);
        using var client = await Login(a);
        var path = $"/ligan/atgard?action=enroll&season={next.Id}";
        Assert.Contains($"action=enroll&amp;season={next.Id}", await client.GetStringAsync("/ligan"));
        Assert.Equal(HttpStatusCode.Found, (await Post(client, path, "league-action", new()
        { ["Input.Action"] = "enroll", ["Input.SeasonId"] = next.Id.ToString() })).StatusCode);
        var row = Assert.Single(await Table());
        Assert.Equal(a.LeagueId, row.PlayerId);
        Assert.Equal((500, 0, 0), (row.ELO, row.Points, row.Matches));
        var registered = (await Rows<LeagueRegistration>()).Single(r => r.SeasonId == next.Id && r.PlayerId == a.LeagueId);
        Assert.True((await Run(a, new() { Action = "enroll", SeasonId = next.Id })).Succeeded);
        Assert.Equal(registered.JoinedUtc, (await Rows<LeagueRegistration>()).Single(r => r.SeasonId == next.Id && r.PlayerId == a.LeagueId).JoinedUtc);
        Assert.True((await Run(a, new() { Action = "join", SessionId = round })).Succeeded);
        Assert.True((await Run(admin, new() { Action = "create", SeasonId = next.Id, Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10) })).Succeeded);
        var previousRound = (await Rows<LeagueSession>()).Max(s => s.Id);
        Assert.False((await Run(a, new() { Action = "join", SessionId = previousRound })).Succeeded);
        Assert.DoesNotContain("action=enroll", await client.GetStringAsync("/ligan"));
        Assert.Equal(2, (await Table(old.Id)).Count);
    }
    [Fact]
    public async Task Deleting_season_removes_all_results_and_replays_later_elo_without_deleting_players()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User(AccessLevels.KeyMember);
        var season = (await Rows<LeagueSeason>()).Single();
        var round = await Round(admin, a, b);
        var match = await Report(a, b, round);
        Assert.True((await MatchAction(b, match, "confirm")).Succeeded);
        Assert.True((await SessionAction(admin, round, "finalize")).Succeeded);
        var byeRound = await Round(admin, a, b, admin);
        Assert.True((await Run(admin, new() { Action = "bye", SessionId = byeRound, PlayerId = a.LeagueId })).Succeeded);
        Assert.True((await SessionAction(admin, byeRound, "finalize")).Succeeded);
        Assert.True((await Run(admin, new() { Action = "close-season", SeasonId = season.Id, Version = season.Version })).Succeeded);
        season = (await Rows<LeagueSeason>()).Single();
        Assert.True((await Run(admin, new() { Action = "create-season", Year = 2027, Term = LeagueTerm.Spring })).Succeeded);
        var next = (await Rows<LeagueSeason>()).Single(s => !s.IsClosed);
        Assert.Empty(await Table());
        foreach (var player in new[] { a, b }) Assert.True((await Run(player, new() { Action = "enroll", SeasonId = next.Id })).Succeeded);
        var nextRound = await Round(admin, a, b);
        var nextMatch = await Report(a, b, nextRound);
        Assert.True((await MatchAction(b, nextMatch, "confirm")).Succeeded);
        Assert.True((await SessionAction(admin, nextRound, "finalize")).Succeeded);
        Assert.Equal(520, (await Table()).Single(r => r.PlayerId == a.LeagueId).ELO);
        var delete = new LeagueCommand { Action = "delete-season", SeasonId = season.Id, Version = season.Version, Confirmed = true };
        Assert.False((await Run(a, delete)).Succeeded);
        Assert.False((await Run(b, delete)).Succeeded);
        delete.Confirmed = false;
        Assert.False((await Run(admin, delete)).Succeeded);
        delete.Confirmed = true; delete.Version = "stale";
        Assert.False((await Run(admin, delete)).Succeeded);
        Assert.Equal(2, (await Rows<LeagueSeason>()).Count);
        delete.Version = season.Version;
        Assert.True((await Run(admin, delete)).Succeeded);
        Assert.Equal(next.Id, Assert.Single(await Rows<LeagueSeason>()).Id);
        Assert.Equal(nextRound, Assert.Single(await Rows<LeagueSession>()).Id);
        Assert.Equal(nextMatch, Assert.Single(await Rows<LeagueMatch>()).Id);
        Assert.All(await Rows<LeagueParticipation>(), p => { Assert.Equal(nextRound, p.SessionId); Assert.False(p.IsBye); });
        Assert.All(await Rows<LeagueRating>(), r => Assert.Equal(nextMatch, r.MatchId));
        var row = (await Table()).Single(r => r.PlayerId == a.LeagueId);
        Assert.Equal((510, 3, 1), (row.ELO, row.Points, row.Matches));
        Assert.Equal(3, (await Rows<LeaguePlayer>()).Count);
        Assert.Equal(3, (await Rows<ApplicationUser>()).Count);
        Assert.True((await Run(admin, new() { Action = "delete-season", SeasonId = next.Id, Version = next.Version, Confirmed = true })).Succeeded);
        Assert.Empty(await Rows<LeagueSession>()); Assert.Empty(await Rows<LeagueMatch>());
        Assert.Empty(await Rows<LeagueParticipation>()); Assert.Empty(await Rows<LeagueRating>());
        Assert.NotEmpty(await Rows<LeagueAudit>());
        Assert.True((await Run(admin, new() { Action = "create-season", Year = 2026, Term = LeagueTerm.Autumn })).Succeeded);
        Assert.Empty(await Table());
        foreach (var player in new[] { a, b }) Assert.True((await Run(player, new() { Action = "enroll" })).Succeeded);
        Assert.All(await Table(), r => { Assert.Equal(500, r.ELO); Assert.Equal(0, r.Points); });
    }

    [Fact]
    public async Task Delete_season_form_requires_confirmation_and_admin_access()
    {
        var admin = await User(AccessLevels.Admin); var member = await User();
        var season = (await Rows<LeagueSeason>()).Single();
        using var client = await Login(admin); using var player = await Login(member);
        var path = $"/ligan/atgard?action=delete-season&season={season.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync(path)).StatusCode);
        Assert.Contains("action=delete-season", await client.GetStringAsync("/hantera/ligan"));
        var fields = new Dictionary<string, string> { ["Input.Action"] = "delete-season", ["Input.SeasonId"] = season.Id.ToString(), ["Input.Version"] = season.Version };
        Assert.Equal(HttpStatusCode.OK, (await Post(client, path, "league-action", fields)).StatusCode);
        Assert.Single(await Rows<LeagueSeason>());
        fields["Input.Confirmed"] = "true";
        Assert.Equal(HttpStatusCode.Found, (await Post(client, path, "league-action", fields)).StatusCode);
        Assert.Empty(await Rows<LeagueSeason>());
    }
    [Fact]
    public async Task Seasons_reset_statistics_carry_elo_and_replay_corrections_across_seasons()
    {
        var admin = await User(AccessLevels.Admin); var a = await User(); var b = await User();
        var season = (await Rows<LeagueSeason>()).Single();
        var round = await Round(admin, a, b);
        var match = await Report(a, b, round);
        var close = new LeagueCommand { Action = "close-season", SeasonId = season.Id, Version = season.Version };
        Assert.False((await Run(a, close)).Succeeded);
        Assert.False((await Run(admin, close)).Succeeded); // Open round must be resolved first.
        Assert.False((await Run(admin, new() { Action = "create-season", Year = 2027, Term = LeagueTerm.Spring })).Succeeded);
        Assert.True((await MatchAction(b, match, "confirm")).Succeeded);
        Assert.True((await SessionAction(admin, round, "finalize")).Succeeded);
        var byeRound = await Round(admin, a, b, admin);
        Assert.True((await Run(admin, new() { Action = "bye", SessionId = byeRound, PlayerId = b.LeagueId })).Succeeded);
        Assert.True((await SessionAction(admin, byeRound, "finalize")).Succeeded);
        Assert.True((await Run(admin, close)).Succeeded);
        Assert.False((await Run(admin, close)).Succeeded);
        Assert.False((await SessionAction(admin, round, "reopen")).Succeeded);
        Assert.False((await MatchAction(a, match, "edit")).Succeeded);
        Assert.False((await Run(admin, new() { Action = "create", SeasonId = season.Id, Date = new(2027, 1, 1) })).Succeeded);
        Assert.False((await Run(admin, new() { Action = "create-season", Year = 2026, Term = LeagueTerm.Autumn })).Succeeded);
        Assert.False((await Run(b, new() { Action = "create-season", Year = 2027, Term = LeagueTerm.Spring })).Succeeded);
        Assert.True((await Run(admin, new() { Action = "create-season", Year = 2027, Term = LeagueTerm.Spring })).Succeeded);
        var next = (await Rows<LeagueSeason>()).Single(s => !s.IsClosed);
        Assert.Equal("40K-liga Vårterminen 2027", next.Title);
        Assert.Empty(await Table());
        foreach (var player in new[] { a, b }) Assert.True((await Run(player, new() { Action = "enroll", SeasonId = next.Id })).Succeeded);
        var standings = await Table();
        Assert.All(standings, r => Assert.Equal((0, 0, 0, 0, 0, 0m), (r.Points, r.Matches, r.W, r.D, r.L, r.PPM)));
        Assert.Equal(510, standings.Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.Equal(490, standings.Single(r => r.PlayerId == b.LeagueId).ELO);
        var archive = await Table(season.Id);
        Assert.Equal(4, archive.Single(r => r.PlayerId == b.LeagueId).Points);
        var newcomer = await User();
        Assert.DoesNotContain(await Table(season.Id), r => r.PlayerId == newcomer.LeagueId);
        Assert.Equal(500, (await Table()).Single(r => r.PlayerId == newcomer.LeagueId).ELO);
        var newRound = await Round(admin, a, b);
        var newMatch = await Report(a, b, newRound);
        Assert.True((await MatchAction(b, newMatch, "confirm")).Succeeded);
        Assert.True((await SessionAction(admin, newRound, "finalize")).Succeeded);
        Assert.Equal(520, (await Table()).Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.Equal(510, (await Table(season.Id)).Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.True((await MatchAction(admin, match, "edit", LeagueOutcome.PlayerTwoWin)).Succeeded);
        Assert.Equal(490, (await Table(season.Id)).Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.Equal(500, (await Table()).Single(r => r.PlayerId == a.LeagueId).ELO);
        Assert.Equal(3, (await Table()).Single(r => r.PlayerId == a.LeagueId).Points);
        Assert.Equal(1, (await Table()).Single(r => r.PlayerId == a.LeagueId).Matches);
    }

    [Fact]
    public async Task League_page_starts_with_named_table_and_distinguishes_missing_access_from_logged_out()
    {
        var admin = await User(AccessLevels.Admin); var noRole = await User(null, false);
        using var visitor = NewClient(); using var loggedIn = await Login(noRole); using var manager = await Login(admin);
        var anonymousHtml = WebUtility.HtmlDecode(await visitor.GetStringAsync("/ligan"));
        Assert.Contains("Logga in för att delta", anonymousHtml);
        Assert.DoesNotContain("Du saknar behörighet att delta", anonymousHtml);
        var html = WebUtility.HtmlDecode(await loggedIn.GetStringAsync("/ligan"));
        Assert.Contains("Du saknar behörighet att delta eftersom det krävs ett terminspass eller nyckelmedlemskap.", html);
        Assert.DoesNotContain("href=\"/login\"", html);
        Assert.Contains("<caption><h1>40K-liga Höstterminen 2026</h1></caption>", html);
        Assert.DoesNotContain("Vinst 3 poäng", html);
        Assert.True(html.IndexOf("</table>", StringComparison.Ordinal) < html.IndexOf("Du saknar behörighet", StringComparison.Ordinal));
        var hub = await manager.GetStringAsync("/hantera");
        Assert.DoesNotContain("href=\"/ligan/spela\"", hub);
        Assert.Contains("href=\"/hantera/ligan\"", hub);
        var season = (await Rows<LeagueSeason>()).Single();
        Assert.Equal(HttpStatusCode.Found, (await Post(manager, $"/ligan/atgard?action=close-season&season={season.Id}", "league-action", new()
        { ["Input.Action"] = "close-season", ["Input.SeasonId"] = season.Id.ToString(), ["Input.Version"] = season.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Found, (await Post(manager, "/ligan/atgard?action=create-season", "league-action", new()
        { ["Input.Action"] = "create-season", ["Input.Year"] = "2027", ["Input.Term"] = "Spring" })).StatusCode);
        Assert.Contains("40K-liga Vårterminen 2027", WebUtility.HtmlDecode(await visitor.GetStringAsync("/ligan")));
        Assert.Contains("40K-liga Vårterminen 2027</h1>", WebUtility.HtmlDecode(await visitor.GetStringAsync($"/ligan?SeasonId={season.Id}&Sort=ELO")));
    }

    [Fact]
    public async Task Season_migration_preserves_existing_rounds_results_ratings_and_constraints()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        await migrator.MigrateAsync("20260918113130_AddLeagueJoinDate");
        db.Users.AddRange(new ApplicationUser { Id = "a", UserName = "A" }, new ApplicationUser { Id = "b", UserName = "B" });
        await db.SaveChangesAsync();
        db.LeaguePlayers.AddRange(new LeaguePlayer { Id = 1, UserId = "a" }, new LeaguePlayer { Id = 2, UserId = "b" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO LeagueSessions (Id, Date, Status, Version) VALUES (1, '2026-03-01', 1, 'v1'), (2, '2026-09-01', 0, 'v2');
            INSERT INTO LeagueMatches (Id, SessionId, PlayerOneId, PlayerTwoId, ReporterId, Outcome, Status, Version) VALUES (1, 1, 1, 2, 1, 0, 1, 'm');
            INSERT INTO LeagueParticipations (SessionId, PlayerId, MatchId, IsBye) VALUES (1, 1, 1, 0), (1, 2, 1, 0);
            INSERT INTO LeagueRatings (MatchId, PlayerId, Before, Change, After) VALUES (1, 1, 500, 10, 510), (1, 2, 500, -10, 490);
            """);
        await migrator.MigrateAsync();
        var rounds = await db.LeagueSessions.Include(s => s.Season).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(LeagueTerm.Spring, rounds[0].Season.Term);
        Assert.True(rounds[0].Season.IsClosed);
        Assert.Equal(LeagueTerm.Autumn, rounds[1].Season.Term);
        Assert.False(rounds[1].Season.IsClosed);
        Assert.Single(await db.LeagueMatches.ToListAsync());
        Assert.Equal(2, await db.LeagueParticipations.CountAsync());
        Assert.Equal(2, await db.LeagueRatings.CountAsync());
        var registrations = await db.LeagueRegistrations.ToListAsync();
        Assert.Equal(2, registrations.Count);
        Assert.All(registrations, r => Assert.Equal(rounds[0].SeasonId, r.SeasonId));
        Assert.DoesNotContain(registrations, r => r.SeasonId == rounds[1].SeasonId);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO LeagueMatches (SessionId, PlayerOneId, PlayerTwoId, ReporterId, Outcome, Status, Version) VALUES (1, 2, 1, 2, 0, 0, 'duplicate')"));
    }
    private async Task<HttpClient> Login(Player player)
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Found, (await Post(client, "/login", "login", new() { ["Input.Email"] = player.Email, ["Input.Password"] = "Test-password42!" })).StatusCode);
        return client;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string handler, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(path);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        fields["_handler"] = handler;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    public void Dispose() => factory.Dispose();
}
