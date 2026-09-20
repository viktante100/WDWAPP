using WDWAPP.Data;
using WDWAPP.Services;
using Xunit;

namespace WDWAPP.Tests;

public class PlayerStatisticsTests
{
    private static LeagueMatch Match(int one, int two, LeagueOutcome outcome, int? pointsOne, int? pointsTwo,
        string? factionOne = null, string? factionTwo = null) => new()
    {
        PlayerOneId = one, PlayerTwoId = two, Outcome = outcome,
        PlayerOnePoints = pointsOne, PlayerTwoPoints = pointsTwo,
        PlayerOneFaction = factionOne, PlayerTwoFaction = factionTwo,
        Status = LeagueMatchStatus.Confirmed, Session = new() { Status = LeagueSessionStatus.Finalized }
    };

    [Fact]
    public void Statistics_use_both_sides_and_exclude_unconfirmed_cancelled_and_unrelated_matches()
    {
        var pending = Match(1, 2, LeagueOutcome.PlayerOneWin, 100, 0);
        pending.Status = LeagueMatchStatus.Pending;
        var cancelled = Match(1, 2, LeagueOutcome.PlayerOneWin, 100, 0);
        cancelled.Session.Status = LeagueSessionStatus.Cancelled;
        var stats = PlayerStatistics.Calculate(1, [
            Match(1, 2, LeagueOutcome.PlayerOneWin, 80, 40, "Orks"),
            Match(2, 1, LeagueOutcome.PlayerTwoWin, 20, 60, null, "Orks"),
            Match(2, 1, LeagueOutcome.PlayerOneWin, 90, 40, null, "T'au Empire"),
            Match(1, 2, LeagueOutcome.Draw, null, null), pending, cancelled,
            Match(3, 4, LeagueOutcome.PlayerOneWin, 100, 0)]);
        Assert.Equal(4, stats.Played);
        Assert.Equal(2, stats.Won);
        Assert.Equal(1, stats.Lost);
        Assert.Equal(2d.ToString("0.00"), stats.WinLossRatio);
        Assert.Equal(60d, stats.PointsFor);
        Assert.Equal(50d, stats.PointsAgainst);
        Assert.Equal("Orks", stats.MostPlayedFaction);
    }

    [Fact]
    public void Empty_history_and_zero_losses_have_explicit_displays_and_tied_factions_are_preserved()
    {
        var empty = PlayerStatistics.Calculate(1, []);
        Assert.Equal("–", empty.WinLossRatio);
        Assert.Null(empty.PointsFor);
        Assert.Null(empty.PointsAgainst);
        Assert.Equal("–", empty.MostPlayedFaction);
        var wins = PlayerStatistics.Calculate(1, [
            Match(1, 2, LeagueOutcome.PlayerOneWin, null, null, "Orks"),
            Match(1, 2, LeagueOutcome.Draw, 0, 0, "Aeldari")]);
        Assert.Equal("∞", wins.WinLossRatio);
        Assert.Equal(0d, wins.PointsFor);
        Assert.Equal("Aeldari, Orks", wins.MostPlayedFaction);
    }
}
