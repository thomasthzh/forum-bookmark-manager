namespace Rfbm.Core.Models;

public sealed record BookmarkPost(
    string Id,
    string Url,
    string Title,
    string Type,
    PostStatus Status,
    DateTimeOffset? FavoriteTime)
{
  public ThreadKey Key { get; init; }

  public string PostUrl => Url;

  public string ProjectType => Type;

  public string CanonicalUrl { get; init; } = Url;

  public string ContentUrl { get; init; } = Url;

  public string? FavoriteSourceUrl { get; init; }

  public string? RawFavoriteTime { get; init; }

  public Observed<int> DownloadCount { get; init; } = Observed<int>.Unobservable();

  public Observed<int> VisitCount { get; init; } = Observed<int>.Unobservable();

  public Observed<int> FavoriteCount { get; init; } = Observed<int>.Unobservable();

  public Observed<string> ExtractPassword { get; init; } = Observed<string>.Unobservable();

  public Observed<string> AccessCode { get; init; } = Observed<string>.Unobservable();

  public Observed<string> BodyText { get; init; } = Observed<string>.Unobservable();

  public IReadOnlyList<PostImage> Images { get; init; } = Array.Empty<PostImage>();

  public IReadOnlyList<ResourceLink> ResourceLinks { get; init; } = Array.Empty<ResourceLink>();

  public IReadOnlyList<ResourceLink> DownloadLinks => ResourceLinks;
}
