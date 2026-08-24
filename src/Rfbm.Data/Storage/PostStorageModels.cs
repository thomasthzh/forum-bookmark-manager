using Rfbm.Core.Models;

namespace Rfbm.Data.Storage;

public readonly record struct PostKey(string SiteFamily, string ThreadId)
{
  public override string ToString() => $"{SiteFamily}:{ThreadId}";
}

public readonly record struct Observation<T>(bool IsObserved, T? Value)
{
  public static Observation<T> Unobserved => default;

  public static Observation<T> Observed(T? value) => new(true, value);
}

public sealed record PostCapture
{
  public required PostKey Key { get; init; }
  public required string CanonicalUrl { get; init; }
  public Observation<string?> RawUrl { get; init; }
  public Observation<string?> MirrorUrl { get; init; }
  public Observation<string?> FavoriteUrl { get; init; }
  public Observation<string> Title { get; init; }
  public Observation<string> ProjectType { get; init; }
  public Observation<string?> FavoriteTimeRaw { get; init; }
  public Observation<DateTimeOffset?> FavoriteTimeUtc { get; init; }
  public Observation<int?> DownloadCount { get; init; }
  public Observation<int?> VisitCount { get; init; }
  public Observation<int?> FavoriteCount { get; init; }
  public Observation<string?> ExtractPassword { get; init; }
  public Observation<string?> BodyText { get; init; }
  public Observation<IReadOnlyList<CapturedPostImage>> Images { get; init; }
  public Observation<IReadOnlyList<CapturedResourceLink>> ResourceLinks { get; init; }
}

public sealed record CapturedPostImage(
    int Position,
    string SourceUrl,
    string? LocalPath = null,
    string? ThumbnailPath = null,
    string DownloadStatus = "pending",
    long CaptureGeneration = 0,
    bool IsFallback = false);

public sealed record CapturedResourceLink(
    int Position,
    string Url,
    string Label = "",
    string ContextText = "");

public sealed record StoredPost(
    long RowId,
    PostKey Key,
    string CanonicalUrl,
    string? RawUrl,
    string? MirrorUrl,
    string? FavoriteUrl,
    string Title,
    string ProjectType,
    PostStatus Status,
    string? FavoriteTimeRaw,
    DateTimeOffset? FavoriteTimeUtc,
    int? DownloadCount,
    int? VisitCount,
    int? FavoriteCount,
    string? ExtractPassword,
    string? BodyText,
    bool IsStarred,
    string Note,
    DateTimeOffset? DeletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastCrawledAt,
    IReadOnlyList<CapturedPostImage> Images,
    IReadOnlyList<CapturedResourceLink> ResourceLinks,
    IReadOnlyList<string> Tags);

public enum PostSort
{
  NewestFavorite,
  OldestFavorite,
  RecentlyUpdated
}

public sealed record PostQuery
{
  public string? Search { get; init; }
  public string? ProjectType { get; init; }
  public PostStatus? Status { get; init; }
  public string? Tag { get; init; }
  public bool OnlyStarred { get; init; }
  public bool IncludeTrashed { get; init; }
  public bool OnlyTrashed { get; init; }
  public PostSort Sort { get; init; } = PostSort.NewestFavorite;
  public int Offset { get; init; }
  public int Limit { get; init; } = 100;
}

public sealed record PostPage(
    IReadOnlyList<StoredPost> Items,
    int Total,
    int Offset,
    int Limit);

public sealed record PurgeResult(
    int PurgedCount,
    IReadOnlyList<string> LocalMediaPaths);

public sealed record CaptureSnapshotMetadata
{
  public required string RunId { get; init; }
  public required string WorkItemKey { get; init; }
  public required PostKey PostKey { get; init; }
  public required string RequestedUrl { get; init; }
  public required string FinalUrl { get; init; }
  public required string Kind { get; init; }
  public required string Sha256 { get; init; }
  public required DateTimeOffset CapturedAt { get; init; }
  public required long ByteCount { get; init; }
  public bool MediaPending { get; init; }
  public string? WorkParametersJson { get; init; }
}

public sealed record StoredCaptureSnapshot(
    long SnapshotId,
    string RunId,
    string WorkItemKey,
    long PostRowId,
    PostKey PostKey,
    string RequestedUrl,
    string FinalUrl,
    string Kind,
    string Sha256,
    DateTimeOffset CapturedAt,
    long ByteCount,
    long CaptureGeneration,
    int ExpectedImageCount,
    string WorkState,
    bool MediaPending,
    string? WorkParametersJson);

public sealed record CaptureFinalizeRequest
{
  public required string RunId { get; init; }
  public required string WorkItemKey { get; init; }
  public required PostKey PostKey { get; init; }
  public required long SnapshotId { get; init; }
  public required long CaptureGeneration { get; init; }
}

public sealed record ImageDownloadUpdateRequest
{
  public required PostKey PostKey { get; init; }
  public required long SnapshotId { get; init; }
  public required long CaptureGeneration { get; init; }
  public required string SourceUrl { get; init; }
  public string? LocalPath { get; init; }
  public string? ThumbnailPath { get; init; }
  public required string DownloadStatus { get; init; }
}
