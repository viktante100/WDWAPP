using WDWAPP.Data;

namespace WDWAPP.Services;

public sealed record PlayerStatistics(int Played, int Won, int Lost, double? PointsFor, double? PointsAgainst, string MostPlayedFaction)
{
    public string WinLossRatio => Lost > 0 ? ((double)Won / Lost).ToString("0.00") : Won > 0 ? "∞" : "–";

    public static PlayerStatistics Calculate(int playerId, IEnumerable<LeagueMatch> matches)
    {
        var results = matches.Where(m => m.Status == LeagueMatchStatus.Confirmed
            && m.Session.Status != LeagueSessionStatus.Cancelled
            && (m.PlayerOneId == playerId || m.PlayerTwoId == playerId)).ToList();
        var won = results.Count(m => m.Outcome == (m.PlayerOneId == playerId ? LeagueOutcome.PlayerOneWin : LeagueOutcome.PlayerTwoWin));
        var lost = results.Count(m => m.Outcome == (m.PlayerOneId == playerId ? LeagueOutcome.PlayerTwoWin : LeagueOutcome.PlayerOneWin));
        var factions = results.Select(m => m.PlayerOneId == playerId ? m.PlayerOneFaction : m.PlayerTwoFaction)
            .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!.Trim())
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var mostPlayed = factions.Count == 0 ? "–" : string.Join(", ", factions
            .Where(g => g.Count() == factions.Max(f => f.Count())).Select(g => g.Key).OrderBy(f => f));
        return new(results.Count, won, lost,
            results.Average(m => (double?)(m.PlayerOneId == playerId ? m.PlayerOnePoints : m.PlayerTwoPoints)),
            results.Average(m => (double?)(m.PlayerOneId == playerId ? m.PlayerTwoPoints : m.PlayerOnePoints)), mostPlayed);
    }
}
