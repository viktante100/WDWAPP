namespace WDWAPP.Data;

public enum LeagueSessionStatus { Open, Finalized, Cancelled }
public enum LeagueMatchStatus { Pending, Confirmed, Disputed }
public enum LeagueOutcome { PlayerOneWin, Draw, PlayerTwoWin }
public enum LeagueTerm { Spring = 1, Autumn = 2 }

public sealed class LeagueSeason
{
    public int Id { get; set; }
    public int Year { get; set; }
    public LeagueTerm Term { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    public string Title => $"40K-liga {(Term == LeagueTerm.Spring ? "Vårterminen" : "Höstterminen")} {Year}";
}

// Separate identity from league history, leaving room for a future public presentation.
public sealed class LeaguePlayer
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTime JoinedUtc { get; set; } = DateTime.UtcNow;
    public ApplicationUser User { get; set; } = null!;
}
public sealed class LeagueRegistration
{
    public int SeasonId { get; set; }
    public LeagueSeason Season { get; set; } = null!;
    public int PlayerId { get; set; }
    public LeaguePlayer Player { get; set; } = null!;
    public DateTime JoinedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class LeagueSession
{
    public int Id { get; set; }
    public int SeasonId { get; set; }
    public LeagueSeason Season { get; set; } = null!;
    public DateOnly Date { get; set; }
    public LeagueSessionStatus Status { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class LeagueParticipation
{
    public int SessionId { get; set; }
    public LeagueSession Session { get; set; } = null!;
    public int PlayerId { get; set; }
    public LeaguePlayer Player { get; set; } = null!;
    public int? MatchId { get; set; }
    public LeagueMatch? Match { get; set; }
    public bool IsBye { get; set; }
}
public sealed class LeagueMatch
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public LeagueSession Session { get; set; } = null!;
    public int PlayerOneId { get; set; }
    public int PlayerTwoId { get; set; }
    public int ReporterId { get; set; }
    public LeagueOutcome Outcome { get; set; }
    public string? PlayerOneFaction { get; set; }
    public string? PlayerTwoFaction { get; set; }
    public int? PlayerOnePoints { get; set; }
    public int? PlayerTwoPoints { get; set; }
    public LeagueMatchStatus Status { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class LeagueRating
{
    public int MatchId { get; set; }
    public LeagueMatch Match { get; set; } = null!;
    public int PlayerId { get; set; }
    public LeaguePlayer Player { get; set; } = null!;
    public int Before { get; set; }
    public int Change { get; set; }
    public int After { get; set; }
}
public sealed class LeagueAudit
{
    public int Id { get; set; }
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public string ActorId { get; set; } = "";
    public string Description { get; set; } = "";
}
