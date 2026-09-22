using WDWAPP.Data;

namespace WDWAPP.Services;

public static class CalendarPresentation
{
    public static string Label(CalendarEventType type) => type switch
    {
        CalendarEventType.MatchRequest => "Matchsök", CalendarEventType.LeagueRound => "Ligaomgång",
        CalendarEventType.Tournament => "Turnering", _ => "Speldag"
    };
    public static string CssClass(CalendarEventType type) => type switch
    {
        CalendarEventType.MatchRequest => "event-match", CalendarEventType.LeagueRound => "event-league",
        CalendarEventType.Tournament => "event-tournament", _ => "event-game-day"
    };
    // Project nicknames only, never profile images, private account names or contact data.
    public static IQueryable<CalendarEvent> Summaries(this IQueryable<CalendarEvent> query) => query.Select(e => new CalendarEvent
    {
        Id = e.Id, Title = e.Title, Date = e.Date, Time = e.Time, Type = e.Type, LeagueSessionId = e.LeagueSessionId,
        MatchRequestId = e.MatchRequestId,
        MatchRequest = e.MatchRequest == null ? null : new MatchRequest
        {
            Id = e.MatchRequest.Id, Game = e.MatchRequest.Game, AcceptedByUserId = e.MatchRequest.AcceptedByUserId,
            OwnerUser = new ApplicationUser { UserName = e.MatchRequest.OwnerUser.UserName },
            AcceptedByUser = e.MatchRequest.AcceptedByUser == null ? null
                : new ApplicationUser { UserName = e.MatchRequest.AcceptedByUser.UserName }
        }
    });
}
