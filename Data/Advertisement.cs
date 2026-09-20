using System.ComponentModel.DataAnnotations;

namespace WDWAPP.Data;

public enum AdvertisementType { Sell, Buy, Trade, GiveAway }
public enum AdvertisementContactType { Email, Phone, Sms, Messenger, Discord, AtVenue }

public sealed class Advertisement
{
    public int Id { get; set; }
    public string OwnerUserId { get; set; } = "";
    [MaxLength(160)] public string Title { get; set; } = "";
    [MaxLength(10000)] public string Description { get; set; } = "";
    public AdvertisementType Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    public List<AdvertisementImage> Images { get; set; } = [];
    public List<AdvertisementContactMethod> Contacts { get; set; } = [];
}

public sealed class AdvertisementImage
{
    public int Id { get; set; }
    public int AdvertisementId { get; set; }
    public Advertisement Advertisement { get; set; } = null!;
    public int SortOrder { get; set; }
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "";
}

public sealed class AdvertisementContactMethod
{
    public int Id { get; set; }
    public int AdvertisementId { get; set; }
    public Advertisement Advertisement { get; set; } = null!;
    public AdvertisementContactType Type { get; set; }
    [MaxLength(254)] public string? Value { get; set; }
}
