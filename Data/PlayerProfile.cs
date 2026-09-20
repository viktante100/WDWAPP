using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WDWAPP.Data;

public sealed class PlayerProfile
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    [MaxLength(100)] public string DisplayName { get; set; } = "";
    public bool FirstNameOnly { get; set; } = true;
    public double ImageZoom { get; set; } = 1;
    public double ImageX { get; set; } = 50;
    public double ImageY { get; set; } = 25;
    public static string PublicName(string? fullName, string? nickname, bool firstNameOnly)
    {
        var name = string.IsNullOrWhiteSpace(fullName) ? nickname ?? "Spelare" : fullName.Trim();
        return firstNameOnly ? name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Spelare" : name;
    }
    [MaxLength(100)] public string? Nickname { get; set; }
    [MaxLength(100)] public string? VeizlaIdentity { get; set; }
    public string Avatar { get; set; } = "person-circle";
    public byte[]? ImageData { get; set; }
    public string? ImageContentType { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    // Deliberately separate from verified league results and future verified titles.
    public SelfReportedTitles SelfReportedTitles { get; set; } = new();
    [NotMapped] public string ImageUrl => ImageData is null
        ? $"/Images/avatars/{Avatar}.svg" : $"/spelare/{Id}/bild?v={Version}";
}

public sealed class SelfReportedTitles
{
    public int LeagueWins { get; set; }
    public int RttWins { get; set; }
    public int ChampionshipWins { get; set; }
}
