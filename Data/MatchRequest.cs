using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WDWAPP.Data;

public sealed class MatchRequest
{
    public int Id { get; set; }
    public string OwnerUserId { get; set; } = "";
    public ApplicationUser OwnerUser { get; set; } = null!;
    public string? AcceptedByUserId { get; set; }
    public ApplicationUser? AcceptedByUser { get; set; }
    [MaxLength(80)] public string Game { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? AcceptedUtc { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    // Date/time live only on the existing calendar event, avoiding duplicated schedules.
    public CalendarEvent CalendarEvent { get; set; } = null!;
    [NotMapped] public string DisplayTitle => AcceptedByUserId != null
        ? $"Bokad match – {OwnerUser.UserName} / {AcceptedByUser?.UserName} – {Game}"
        : $"{OwnerUser.UserName} – {Game}";
}

public static class MatchGames
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { "Warhammer 40,000", "Age of Sigmar", "The Old World", "Kill Team", "Blood Bowl" });
}
