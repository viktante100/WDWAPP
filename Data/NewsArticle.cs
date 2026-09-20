namespace WDWAPP.Data;

public sealed class NewsArticle
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string? ImageDescription { get; set; }
    public byte[]? ImageData { get; set; }
    public string? ImageContentType { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? DisplayImageUrl => EventId.HasValue ? Event?.DisplayImageUrl : ImageData is not null ? $"/nyheter/{Id}/bild?v={Version}" : ImageUrl;
    public int? EventId { get; set; }
    public CalendarEvent? Event { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public string DisplayTitle => Event?.Title ?? Title;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public string DisplayBody => Event?.Description ?? Body;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public string DisplayLinks => EventId.HasValue ? Event?.ExternalLink ?? "" : Links;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public string? DisplayImageDescription => EventId.HasValue ? Event?.ImageDescription : ImageDescription;
    public string Links { get; set; } = "";
    public DateTime PublishedUtc { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}
