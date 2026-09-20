using WDWAPP.Data;
namespace WDWAPP.Services;
public static class LeagueLabels
{
    public static string Session(LeagueSessionStatus status) => status switch { LeagueSessionStatus.Open => "Öppen", LeagueSessionStatus.Finalized => "Slutförd – ELO fastställd", _ => "Inställd" };
    public static string Match(LeagueMatchStatus status) => status switch { LeagueMatchStatus.Pending => "Väntar på motståndaren", LeagueMatchStatus.Confirmed => "Bekräftad", _ => "Bestridd – räknas inte i tabellen" };
    public static string Outcome(LeagueMatch match, string one, string two) => match.Outcome switch { LeagueOutcome.PlayerOneWin => $"Vinst för {one}", LeagueOutcome.PlayerTwoWin => $"Vinst för {two}", _ => "Oavgjort" };
}
