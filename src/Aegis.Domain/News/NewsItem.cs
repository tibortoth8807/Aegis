namespace Aegis.Domain.News;

/// <summary>An announcement on the portal's start page.</summary>
public sealed class NewsItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Plain text with blank-line paragraphs.</summary>
    public string Body { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public int AuthorId { get; set; }
    public bool Pinned { get; set; }
}
