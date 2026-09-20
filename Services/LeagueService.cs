using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WDWAPP.Data;
using WDWAPP.Security;

namespace WDWAPP.Services;

public sealed class LeagueCommand
{
    public string Action { get; set; } = "";
    public bool Confirmed { get; set; }
    public int SeasonId { get; set; }
    public int Year { get; set; } = DateTime.UtcNow.Year;
    public LeagueTerm Term { get; set; } = DateTime.UtcNow.Month <= 6 ? LeagueTerm.Spring : LeagueTerm.Autumn;
    public int SessionId { get; set; }
    public int MatchId { get; set; }
    public int PlayerId { get; set; }
    public int OpponentId { get; set; }
    public LeagueOutcome Outcome { get; set; }
    public string? PlayerOneFaction { get; set; }
    public string? PlayerTwoFaction { get; set; }
    public int? PlayerOnePoints { get; set; }
    public int? PlayerTwoPoints { get; set; }
    public DateOnly? Date { get; set; }
    public string Version { get; set; } = "";
}
public sealed record LeagueResult(bool Succeeded, string Message);
public sealed record LeagueStanding(int PlayerId, string Player, int Matches, int W, int D, int L,
    decimal PPM, int ELO, int Points, int Position = 0, int? PTSF = 0, int? PTSA = 0)
{
    public decimal PPG => PPM;
}

public static class LeagueRanking
{
    private static readonly int[] Limits = [25, 50, 75, 100, 125, 150, 200, 250, 300, 400, 500, int.MaxValue];
    private static readonly int[] Favorite = [10, 9, 8, 7, 6, 6, 5, 4, 3, 2, 2, 2];
    private static readonly int[] Underdog = [10, 11, 12, 13, 15, 16, 17, 18, 19, 20, 30, 40];
    private static readonly int[] Draw = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15];
    // Delta for player one; player two always receives its exact inverse.
    public static int Delta(int one, int two, LeagueOutcome outcome)
    {
        var bracket = Array.FindIndex(Limits, limit => Math.Abs((long)one - two) <= limit);
        return outcome switch {
            LeagueOutcome.Draw => Math.Sign(two - one) * Draw[bracket],
            LeagueOutcome.PlayerOneWin => one >= two ? Favorite[bracket] : Underdog[bracket],
            LeagueOutcome.PlayerTwoWin => -(two >= one ? Favorite[bracket] : Underdog[bracket]),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
    public static List<LeagueStanding> Sort(IEnumerable<LeagueStanding> rows, string? column, bool ascending)
    {
        Func<LeagueStanding, object> key = column switch {
            "Player" => r => r.Player, "Matches" => r => r.Matches, "W" => r => r.W,
            "D" => r => r.D, "L" => r => r.L, "PPM" => r => r.PPM,
            "PTSF" => r => r.PTSF ?? -1, "PTSA" => r => r.PTSA ?? -1, "PPG" => r => r.PPG,
            "ELO" => r => r.ELO, "Points" => r => r.Points, _ => r => r.Position
        };
        return (ascending ? rows.OrderBy(key) : rows.OrderByDescending(key)).ThenBy(r => r.Position).ToList();
    }
}

public sealed class LeagueService(ApplicationDbContext database, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
{
    public static DateOnly JoinedDate(LeaguePlayer player) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
        DateTime.SpecifyKind(player.JoinedUtc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")));
    public static DateOnly RegistrationDate(LeagueRegistration registration) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
        DateTime.SpecifyKind(registration.JoinedUtc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")));
    private async Task<bool> RegisteredForRound(int playerId, LeagueSession session)
    {
        var registration = await database.LeagueRegistrations.FindAsync(session.SeasonId, playerId);
        return registration is not null && RegistrationDate(registration) <= session.Date;
    }
    public Task<LeagueSeason?> CurrentSeasonAsync() => database.LeagueSeasons.AsNoTracking()
        .OrderBy(s => s.IsClosed).ThenByDescending(s => s.Year).ThenByDescending(s => s.Term).FirstOrDefaultAsync();
    public async Task<List<LeagueStanding>> StandingsAsync(int? seasonId = null)
    {
        var season = seasonId.HasValue ? await database.LeagueSeasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == seasonId) : await CurrentSeasonAsync();
        if (season is null) return [];
        var players = await database.LeaguePlayers.AsNoTracking().Include(p => p.User)
            .Where(p => database.LeagueRegistrations.Any(r => r.SeasonId == season.Id && r.PlayerId == p.Id)).ToListAsync();
        var matches = await database.LeagueMatches.AsNoTracking().Where(m => m.Session.SeasonId == season.Id && m.Status == LeagueMatchStatus.Confirmed && m.Session.Status != LeagueSessionStatus.Cancelled).ToListAsync();
        var byes = await database.LeagueParticipations.AsNoTracking().Where(p => p.Session.SeasonId == season.Id && p.IsBye && p.Session.Status != LeagueSessionStatus.Cancelled).ToListAsync();
        var ratings = await database.LeagueRatings.AsNoTracking().Where(r => r.Match.Session.Season.Year < season.Year
            || (r.Match.Session.Season.Year == season.Year && r.Match.Session.Season.Term <= season.Term)).ToListAsync();
        return players.Select(p => {
            var played = matches.Where(m => m.PlayerOneId == p.Id || m.PlayerTwoId == p.Id).ToList();
            var wins = played.Count(m => m.Outcome == (m.PlayerOneId == p.Id ? LeagueOutcome.PlayerOneWin : LeagueOutcome.PlayerTwoWin));
            var draws = played.Count(m => m.Outcome == LeagueOutcome.Draw);
            var losses = played.Count - wins - draws;
            var points = wins * 3 + draws * 2 + losses;
            var completeScores = played.All(m => m.PlayerOnePoints.HasValue && m.PlayerTwoPoints.HasValue);
            int? pointsFor = completeScores ? played.Sum(m => m.PlayerOneId == p.Id ? m.PlayerOnePoints!.Value : m.PlayerTwoPoints!.Value) : null;
            int? pointsAgainst = completeScores ? played.Sum(m => m.PlayerOneId == p.Id ? m.PlayerTwoPoints!.Value : m.PlayerOnePoints!.Value) : null;
            return new LeagueStanding(p.Id, p.User.UserName!, played.Count, wins, draws, losses,
                played.Count == 0 ? 0 : (decimal)points / played.Count,
                500 + ratings.Where(r => r.PlayerId == p.Id).Sum(r => r.Change), points + 3 * byes.Count(b => b.PlayerId == p.Id),
                PTSF: pointsFor, PTSA: pointsAgainst);
        }).OrderByDescending(r => r.Points).ThenByDescending(r => r.ELO).ThenBy(r => r.PlayerId)
            .Select((r, index) => r with { Position = index + 1 }).ToList();
    }

    public async Task<LeagueResult> ExecuteAsync(ClaimsPrincipal actor, LeagueCommand command)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        database.ChangeTracker.Clear();
        var user = actor.Identity?.IsAuthenticated == true ? await signIn.ValidateSecurityStampAsync(actor) : null;
        if (user is null) return new(false, "Logga in för att delta.");
        if (!await Eligible(user)) return new(false, "Du saknar behörighet att delta eftersom det krävs ett terminspass eller nyckelmedlemskap.");
        var admin = await users.IsInRoleAsync(user, AccessLevels.Admin);
        var before = await Snapshot(command);
        var result = await Apply(user, admin, command);
        if (!result.Succeeded) { database.ChangeTracker.Clear(); return result; }
        await database.SaveChangesAsync();
        if (command.Action is "create" or "date" or "cancel" or "reopen")
            await SyncCalendarAsync(command.SessionId);
        database.LeagueAudits.Add(new() { ActorId = user.Id, Description = System.Text.Json.JsonSerializer.Serialize(new { Command = command, Before = before, After = await Snapshot(command) }) });
        await RecalculateAsync();
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return result;
    }

    private async Task<string> Snapshot(LeagueCommand c) => System.Text.Json.JsonSerializer.Serialize(new {
        Season = await database.LeagueSeasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == c.SeasonId),
        Session = await database.LeagueSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == c.SessionId),
        Match = await database.LeagueMatches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == c.MatchId),
        Participation = await database.LeagueParticipations.AsNoTracking().Where(p => p.SessionId == c.SessionId)
            .Select(p => new { p.PlayerId, p.MatchId, p.IsBye }).ToListAsync()
    });

    private async Task<bool> Eligible(ApplicationUser user) => (await users.GetRolesAsync(user)).Any(AccessLevels.Roles.Contains);
    public const string CalendarDescription = "40K ligaspel. Bara att dyka upp i lokalen eller kontakta Mattias eller Christoffer om du vill veta mer!";
    private async Task SyncCalendarAsync(int sessionId)
    {
        var session = await database.LeagueSessions.SingleAsync(s => s.Id == sessionId);
        var item = await database.Events.SingleOrDefaultAsync(e => e.LeagueSessionId == sessionId);
        if (session.Status == LeagueSessionStatus.Cancelled)
        {
            if (item is not null) database.Events.Remove(item);
            return;
        }
        if (item is null)
        {
            item = new CalendarEvent { LeagueSessionId = sessionId, CreatedUtc = DateTime.UtcNow };
            database.Events.Add(item);
        }
        item.Type = CalendarEventType.LeagueRound;
        item.Title = $"40K-ligan – Omgång {session.Id}";
        item.Description = CalendarDescription;
        item.Date = session.Date;
        item.Time = new TimeOnly(17, 0);
        item.Version = Guid.NewGuid().ToString("N");
    }
    private static LeagueResult Fail(string message) => new(false, message);
    private async Task<LeagueResult> Apply(ApplicationUser user, bool admin, LeagueCommand c)
    {
        if (c.Action == "delete-season")
        {
            if (!admin) return Fail("Endast Admin kan radera en liga.");
            if (!c.Confirmed) return Fail("Bekräfta att ligan och all dess statistik ska raderas permanent.");
            var season = await database.LeagueSeasons.FindAsync(c.SeasonId);
            if (season is null || season.Version != c.Version) return Fail("Ligan har ändrats eller tagits bort. Ladda om sidan.");
            // Remove participation first: its match FK deliberately prevents implicit deletion.
            await database.LeagueParticipations.Where(p => p.Session.SeasonId == season.Id).ExecuteDeleteAsync();
            await database.LeagueRatings.Where(r => r.Match.Session.SeasonId == season.Id).ExecuteDeleteAsync();
            await database.LeagueMatches.Where(m => m.Session.SeasonId == season.Id).ExecuteDeleteAsync();
            await database.LeagueSessions.Where(s => s.SeasonId == season.Id).ExecuteDeleteAsync();
            database.LeagueSeasons.Remove(season);
            return new(true, "Ligan och dess statistik har raderats. ELO räknas om från kvarvarande ligor.");
        }
        if (c.Action == "create-season")
        {
            if (!admin) return Fail("Endast Admin kan skapa en liga.");
            if (c.Year is < 1 or > 9999 || !Enum.IsDefined(c.Term)) return Fail("Välj ett giltigt år och en termin.");
            if (await database.LeagueSeasons.AnyAsync(s => !s.IsClosed)) return Fail("Avsluta den pågående ligan först.");
            if (await database.LeagueSeasons.AnyAsync(s => s.Year > c.Year || (s.Year == c.Year && s.Term >= c.Term)))
                return Fail("Den nya ligan måste avse en senare termin än tidigare ligor.");
            var season = new LeagueSeason { Year = c.Year, Term = c.Term };
            database.LeagueSeasons.Add(season);
            await database.SaveChangesAsync();
            c.SeasonId = season.Id;
            return new(true, "Ligan har skapats.");
        }
        if (c.Action == "close-season")
        {
            if (!admin) return Fail("Endast Admin kan avsluta en liga.");
            var season = await database.LeagueSeasons.FindAsync(c.SeasonId);
            if (season is null || season.IsClosed || season.Version != c.Version) return Fail("Ligan har ändrats. Ladda om sidan.");
            if (await database.LeagueSessions.AnyAsync(s => s.SeasonId == season.Id && s.Status == LeagueSessionStatus.Open))
                return Fail("Slutför eller ställ in alla öppna omgångar innan ligan avslutas.");
            season.IsClosed = true;
            season.ClosedUtc = DateTime.UtcNow;
            season.Version = Guid.NewGuid().ToString("N");
            return new(true, "Ligan är avslutad. ELO följer med till nästa liga.");
        }
        var me = await database.LeaguePlayers.SingleOrDefaultAsync(p => p.UserId == user.Id);
        if (c.Action == "enroll")
        {
            var season = await database.LeagueSeasons.SingleOrDefaultAsync(s => s.Id == c.SeasonId && !s.IsClosed);
            if (season is null) return Fail("Välj en pågående liga att anmäla dig till.");
            if (me is null)
            {
                me = new() { UserId = user.Id };
                database.LeaguePlayers.Add(me);
                await database.SaveChangesAsync();
            }
            if (!await database.LeagueRegistrations.AnyAsync(r => r.SeasonId == season.Id && r.PlayerId == me.Id))
                database.LeagueRegistrations.Add(new() { SeasonId = season.Id, PlayerId = me.Id });
            return new(true, "Du är med i ligan.");
        }
        if (c.Action == "create")
        {
            if (!admin || c.Date is null || c.Date == DateOnly.MinValue) return Fail("Admin och giltigt datum krävs.");
            var season = await database.LeagueSeasons.SingleOrDefaultAsync(s => !s.IsClosed && (c.SeasonId == 0 || s.Id == c.SeasonId));
            if (season is null) return Fail("Skapa en ny liga först. Avslutade ligor kan inte få nya omgångar.");
            var created = new LeagueSession { Date = c.Date.Value, SeasonId = season.Id };
            database.LeagueSessions.Add(created);
            await database.SaveChangesAsync();
            c.SessionId = created.Id;
            return new(true, "Omgången har skapats.");
        }
        var session = await database.LeagueSessions.Include(s => s.Season).SingleOrDefaultAsync(s => s.Id == c.SessionId);
        if (session is null) return Fail("Omgången saknas.");
        c.SeasonId = session.SeasonId;
        if (session.Season.IsClosed && (!admin || c.Action is "join" or "report" or "reopen"))
            return Fail("Ligan är avslutad. Endast administrativa korrigeringar av historiken är tillåtna.");
        if (c.Action is "date" or "finalize" or "reopen" or "cancel")
        {
            if (!admin) return Fail("Endast Admin kan hantera omgångar.");
            if (session.Version != c.Version) return Fail("Omgången har ändrats. Ladda om sidan.");
            if (c.Action == "date")
            {
                if (c.Date is null || c.Date == DateOnly.MinValue) return Fail("Ange ett giltigt datum.");
                var participants = await database.LeagueParticipations.Where(p => p.SessionId == session.Id).Select(p => p.Player).ToListAsync();
                if (participants.Any(p => JoinedDate(p) > c.Date.Value)) return Fail("Datumet kan inte ligga före en deltagares inträde i ligan.");
                var registrations = await database.LeagueRegistrations.Where(r => r.SeasonId == session.SeasonId).ToListAsync();
                if (participants.Any(p => !registrations.Any(r => r.PlayerId == p.Id && RegistrationDate(r) <= c.Date.Value)))
                    return Fail("Datumet kan inte ligga före en deltagares anmälan till den här ligan.");
                session.Date = c.Date.Value;
            }
            if (c.Action == "finalize")
            {
                if (session.Status != LeagueSessionStatus.Open) return Fail("Omgången måste vara öppen.");
                if (await database.LeagueMatches.AnyAsync(m => m.SessionId == session.Id && m.Status != LeagueMatchStatus.Confirmed))
                    return Fail("Bekräfta, korrigera eller ta bort väntande/bestridda matcher först.");
                session.Status = LeagueSessionStatus.Finalized;
            }
            if (c.Action == "reopen") session.Status = LeagueSessionStatus.Open;
            if (c.Action == "cancel") session.Status = LeagueSessionStatus.Cancelled;
            session.Version = Guid.NewGuid().ToString("N");
            return new(true, "Omgången har uppdaterats. ELO räknas om.");
        }
        if (session.Status == LeagueSessionStatus.Cancelled || (!admin && session.Status != LeagueSessionStatus.Open))
            return Fail("Omgången är låst.");
        if (c.Action == "join")
        {
            if (me is null) return Fail("Gå med i ligan först.");
            if (!await RegisteredForRound(me.Id, session)) return Fail("Anmäl dig till den här ligan först. Du kan bara delta i omgångar från och med anmälningsdagen.");
            if (session.Date < JoinedDate(me)) return Fail("Du kan endast delta i omgångar från och med ditt inträde i ligan.");
            if (!await database.LeagueParticipations.AnyAsync(p => p.SessionId == session.Id && p.PlayerId == me.Id))
            {
                if (await database.LeagueParticipations.AnyAsync(p => p.SessionId == session.Id && p.IsBye)) return Fail("Admin måste ta bort WO innan fler spelare kan anmäla sig.");
                database.LeagueParticipations.Add(new() { SessionId = session.Id, PlayerId = me.Id });
            }
            return new(true, "Du är anmäld till omgången.");
        }
        if (c.Action is "bye" or "remove-bye")
        {
            if (!admin) return Fail("Endast Admin kan tilldela WO.");
            var entry = await database.LeagueParticipations.Include(p => p.Player).ThenInclude(p => p.User)
                .SingleOrDefaultAsync(p => p.SessionId == session.Id && p.PlayerId == c.PlayerId);
            if (entry is null || entry.MatchId is not null) return Fail("Spelaren måste vara anmäld utan match.");
            if (c.Action == "bye" && !await Eligible(entry.Player.User)) return Fail("Spelaren saknar medlemsbehörighet.");
            if (c.Action == "bye" && !await RegisteredForRound(entry.PlayerId, session)) return Fail("Spelaren var inte anmäld till ligan vid omgången.");
            if (c.Action == "bye" && session.Date < JoinedDate(entry.Player)) return Fail("WO kan inte tilldelas före spelarens inträde i ligan.");
            if (c.Action == "bye" && (await database.LeagueParticipations.CountAsync(p => p.SessionId == session.Id) % 2 == 0
                || await database.LeagueParticipations.AnyAsync(p => p.SessionId == session.Id && p.IsBye && p.PlayerId != c.PlayerId)))
                return Fail("WO kräver ett udda antal anmälda och får tilldelas högst en spelare per omgång.");
            entry.IsBye = c.Action == "bye";
            return new(true, "WO har uppdaterats.");
        }
        if (!Enum.IsDefined(c.Outcome)) return Fail("Ogiltigt resultat.");
        if (c.Action is "report" or "edit")
        {
            if (string.IsNullOrWhiteSpace(c.PlayerOneFaction) || string.IsNullOrWhiteSpace(c.PlayerTwoFaction)
                || c.PlayerOneFaction.Trim().Length > 100 || c.PlayerTwoFaction.Trim().Length > 100)
                return Fail("Ange faction för båda spelarna (högst 100 tecken).");
            if (c.PlayerOnePoints is null or < 0 || c.PlayerTwoPoints is null or < 0)
                return Fail("Ange poäng för båda spelarna. Poäng får inte vara negativa.");
            c.Outcome = c.PlayerOnePoints == c.PlayerTwoPoints ? LeagueOutcome.Draw
                : c.PlayerOnePoints > c.PlayerTwoPoints ? LeagueOutcome.PlayerOneWin : LeagueOutcome.PlayerTwoWin;
        }
        if (c.Action == "report")
        {
            var one = admin ? c.PlayerId : me?.Id ?? 0;
            if (one == c.OpponentId) return Fail("Du kan inte spela mot dig själv.");
            var entries = await database.LeagueParticipations.Include(p => p.Player).ThenInclude(p => p.User)
                .Where(p => p.SessionId == session.Id && (p.PlayerId == one || p.PlayerId == c.OpponentId)).ToListAsync();
            if (entries.Count != 2 || entries.Any(p => p.MatchId is not null || p.IsBye)) return Fail("Båda måste vara anmälda och sakna match/WO i omgången.");
            foreach (var entry in entries)
                if (!await Eligible(entry.Player.User) || session.Date < JoinedDate(entry.Player) || !await RegisteredForRound(entry.PlayerId, session)) return Fail("Båda spelarna måste ha medlemsbehörighet och ha anmält sig till den här ligan före omgången.");
            var participating = me is not null && (me.Id == one || me.Id == c.OpponentId);
            var match = new LeagueMatch { SessionId = session.Id, PlayerOneId = one, PlayerTwoId = c.OpponentId,
                PlayerOneFaction = c.PlayerOneFaction!.Trim(), PlayerTwoFaction = c.PlayerTwoFaction!.Trim(),
                PlayerOnePoints = c.PlayerOnePoints, PlayerTwoPoints = c.PlayerTwoPoints,
                ReporterId = participating ? me!.Id : one, Outcome = c.Outcome, Status = admin && !participating ? LeagueMatchStatus.Confirmed : LeagueMatchStatus.Pending };
            database.LeagueMatches.Add(match);
            foreach (var entry in entries) entry.Match = match;
            await database.SaveChangesAsync();
            c.MatchId = match.Id;
            return new(true, match.Status == LeagueMatchStatus.Confirmed ? "Matchen har sparats." : "Resultatet väntar på motståndarens bekräftelse.");
        }
        var existing = await database.LeagueMatches.SingleOrDefaultAsync(m => m.Id == c.MatchId && m.SessionId == session.Id);
        if (existing is null) return Fail("Matchen saknas.");
        if (!admin && (me is null || (me.Id != existing.PlayerOneId && me.Id != existing.PlayerTwoId))) return Fail("Du får endast hantera egna matcher.");
        if (!admin && !await RegisteredForRound(me!.Id, session)) return Fail("Du måste vara anmäld till den här ligan för att hantera matchen.");
        if (existing.Version != c.Version) return Fail("Matchen har ändrats. Ladda om sidan.");
        if (c.Action == "delete")
        {
            if (!admin) return Fail("Endast Admin kan ta bort matcher.");
            var entries = await database.LeagueParticipations.Where(p => p.MatchId == existing.Id).ToListAsync();
            foreach (var entry in entries) { entry.Match = null; entry.MatchId = null; }
            // Release the restrictive participation foreign keys before deleting the match.
            await database.SaveChangesAsync();
            database.LeagueMatches.Remove(existing);
        }
        else if (c.Action == "edit")
        {
            if (!admin && existing.Status == LeagueMatchStatus.Confirmed) return Fail("Bekräftade matcher kan endast ändras av Admin.");
            existing.Outcome = c.Outcome;
            existing.PlayerOneFaction = c.PlayerOneFaction!.Trim();
            existing.PlayerTwoFaction = c.PlayerTwoFaction!.Trim();
            existing.PlayerOnePoints = c.PlayerOnePoints;
            existing.PlayerTwoPoints = c.PlayerTwoPoints;
            existing.Status = admin ? LeagueMatchStatus.Confirmed : LeagueMatchStatus.Pending;
            if (!admin) existing.ReporterId = me!.Id;
        }
        else if (c.Action is "confirm" or "dispute")
        {
            if (existing.Status != LeagueMatchStatus.Pending) return Fail("Resultatet väntar inte på bekräftelse.");
            if (me is null || (me.Id != existing.PlayerOneId && me.Id != existing.PlayerTwoId) || existing.ReporterId == me.Id)
                return Fail("Endast motståndaren kan bekräfta eller bestrida resultatet. Admin kan korrigera resultat istället.");
            if (c.Action == "confirm")
            {
                var opponents = await database.LeaguePlayers.Include(p => p.User)
                    .Where(p => p.Id == existing.PlayerOneId || p.Id == existing.PlayerTwoId).ToListAsync();
                foreach (var opponent in opponents)
                    if (!await Eligible(opponent.User)) return Fail("Båda spelarna måste fortfarande ha medlemsbehörighet. Kontakta Admin.");
            }
            existing.Status = c.Action == "confirm" ? LeagueMatchStatus.Confirmed : LeagueMatchStatus.Disputed;
        }
        else return Fail("Ogiltig åtgärd.");
        existing.Version = Guid.NewGuid().ToString("N");
        return new(true, "Matchen har uppdaterats.");
    }

    // Rebuild a derived ledger, never mutate an independent current-rating total.
    // Season then date and session ID defines stable chronological order, including equal-date rounds.
    private async Task RecalculateAsync()
    {
        await database.LeagueRatings.ExecuteDeleteAsync();
        var matches = await database.LeagueMatches.AsNoTracking()
            .Where(m => m.Status == LeagueMatchStatus.Confirmed && m.Session.Status == LeagueSessionStatus.Finalized)
            .OrderBy(m => m.Session.Season.Year).ThenBy(m => m.Session.Season.Term)
            .ThenBy(m => m.Session.Date).ThenBy(m => m.SessionId).ThenBy(m => m.Id).ToListAsync();
        var ratings = new Dictionary<int, int>();
        foreach (var match in matches)
        {
            var one = ratings.GetValueOrDefault(match.PlayerOneId, 500);
            var two = ratings.GetValueOrDefault(match.PlayerTwoId, 500);
            var delta = LeagueRanking.Delta(one, two, match.Outcome);
            foreach (var (id, before, change) in new[] { (match.PlayerOneId, one, delta), (match.PlayerTwoId, two, -delta) })
            {
                ratings[id] = before + change;
                database.LeagueRatings.Add(new() { MatchId = match.Id, PlayerId = id, Before = before, Change = change, After = before + change });
            }
        }
    }
}
