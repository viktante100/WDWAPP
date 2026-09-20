using System.ComponentModel.DataAnnotations.Schema;

namespace WDWAPP.Data;

public enum CalendarEventType { GameDay = 0, Tournament = 1, LeagueRound = 2, MatchRequest = 3 }

public sealed class CalendarEvent
{
    public int Id { get; set; }
    public CalendarEventType Type { get; set; } = CalendarEventType.GameDay;
    public int? MatchRequestId { get; set; }
    public MatchRequest? MatchRequest { get; set; }
    [NotMapped] public string DisplayTitle => MatchRequest?.DisplayTitle ?? Title;
    public int? LeagueSessionId { get; set; }
    public LeagueSession? LeagueSession { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly Date { get; set; }
    // Local wall-clock time in Europe/Stockholm, not a UTC instant.
    public TimeOnly? Time { get; set; }
    public string? ExternalLink { get; set; }
    public byte[]? ImageData { get; set; }
    public string? ImageContentType { get; set; }
    public string? ImageDescription { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    public NewsArticle? NewsArticle { get; set; }
    [NotMapped] public string? DisplayImageUrl => ImageData is null ? null : $"/evenemang/{Id}/bild?v={Version}";
}
