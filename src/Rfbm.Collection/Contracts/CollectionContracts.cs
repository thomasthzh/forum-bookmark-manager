using System.Collections.Immutable;
using Rfbm.Collection.Acquisition;

namespace Rfbm.Collection.Contracts;

public interface ICollectionEngine
{
  ValueTask<ICollectionSession> OpenAsync(
      CollectionIntent intent,
      CancellationToken cancellationToken = default);

  ValueTask<RunSummary?> FindResumeCandidateAsync(
      string databasePath,
      CancellationToken cancellationToken = default);
}

public interface ICollectionSession : IAsyncDisposable
{
  CollectionRunId Id { get; }
  IAsyncEnumerable<CollectionEvent> Events(CancellationToken cancellationToken = default);
  ValueTask RequestCancelAsync(CancellationToken cancellationToken = default);
  Task<CollectionResult> Completion { get; }
}

public abstract record CollectionIntent;

public sealed record StartCollection(CollectionPlan Plan) : CollectionIntent;

public sealed record ResumeCollection(
    string DatabasePath,
    CollectionRunId RunId) : CollectionIntent;

public sealed record CollectionPlan(
    string DatabasePath,
    SiteDefinition Site,
    Uri FavoritesUrl,
    int FavoritePageConcurrency = 2,
    int ThreadConcurrency = 4,
    int MaxSnapshotBytes = 8 * 1024 * 1024,
    int MaxFavoritePages = 10_000,
    bool DownloadPostImages = true,
    string? MediaDirectory = null,
    int ImageConcurrency = 3,
    long MaxImageBytes = 12 * 1024 * 1024)
{
  public void Validate()
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
    if (!FavoritesUrl.IsAbsoluteUri ||
        FavoritesUrl.Scheme is not ("http" or "https"))
    {
      throw new CollectionOpenException(
          CollectionErrorCode.InvalidPlan,
          "收藏页地址必须是绝对 HTTP(S) URL。");
    }

    if (FavoritePageConcurrency is < 1 or > 8 ||
        ThreadConcurrency is < 1 or > 32 ||
        MaxSnapshotBytes is < 1024 or > 32 * 1024 * 1024 ||
        MaxFavoritePages is < 1 or > 100_000 ||
        ImageConcurrency is < 1 or > 8 ||
        MaxImageBytes is < 1024 or > 64 * 1024 * 1024)
    {
      throw new CollectionOpenException(
          CollectionErrorCode.InvalidPlan,
          "采集并发或快照上限超出安全范围。");
    }

    Site.Validate();
    if (!Site.ContainsOrigin(FavoritesUrl))
    {
      throw new CollectionOpenException(
          CollectionErrorCode.InvalidPlan,
          "收藏页不属于已配置站点。");
    }
  }
}

public sealed record SiteDefinition(
    SiteFamily Family,
    Uri PrimaryOrigin,
    ImmutableArray<Uri> MirrorOrigins)
{
  public void Validate()
  {
    if (string.IsNullOrWhiteSpace(Family.Value))
    {
      throw new CollectionOpenException(CollectionErrorCode.InvalidPlan, "站点标识不能为空。");
    }

    var origins = MirrorOrigins.Prepend(PrimaryOrigin).ToArray();
    if (origins.Any(uri => !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")))
    {
      throw new CollectionOpenException(CollectionErrorCode.InvalidPlan, "主站和镜像必须是 HTTP(S) origin。");
    }
  }

  public bool ContainsOrigin(Uri uri) =>
      MirrorOrigins.Prepend(PrimaryOrigin).Any(origin =>
          string.Equals(origin.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(origin.Host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
          origin.Port == uri.Port);
}

public readonly record struct CollectionRunId(Guid Value)
{
  public override string ToString() => Value.ToString("N");
}

public readonly record struct SiteFamily(string Value)
{
  public override string ToString() => Value;
}

public readonly record struct ThreadKey(SiteFamily SiteFamily, string ThreadId)
{
  public override string ToString() => $"{SiteFamily.Value}:{ThreadId}";
}

public enum RunState
{
  Preparing,
  WaitingForUser,
  Discovering,
  Collecting,
  CancelRequested,
  Cancelled,
  Succeeded,
  PartiallySucceeded,
  Interrupted,
  Failed
}

public sealed record RunSummary(
    CollectionRunId Id,
    RunState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int PagesDiscovered,
    int PagesCommitted,
    int PagesFailed,
    int ThreadsDiscovered,
    int ThreadsCommitted,
    int ThreadsFailed,
    bool CanResume);

public abstract record CollectionEvent(
    CollectionRunId RunId,
    long Sequence,
    DateTimeOffset OccurredAt);

public sealed record StateChanged(
    CollectionRunId RunId,
    long Sequence,
    DateTimeOffset OccurredAt,
    RunState State,
    string Message) : CollectionEvent(RunId, Sequence, OccurredAt);

public sealed record ProgressChanged(
    CollectionRunId RunId,
    long Sequence,
    DateTimeOffset OccurredAt,
    int PagesDiscovered,
    int PagesCommitted,
    int PagesFailed,
    int ThreadsDiscovered,
    int ThreadsCommitted,
    int ThreadsFailed) : CollectionEvent(RunId, Sequence, OccurredAt);

public sealed record ItemFailed(
    CollectionRunId RunId,
    long Sequence,
    DateTimeOffset OccurredAt,
    Uri? Uri,
    ThreadKey? Thread,
    CollectionIssue Issue) : CollectionEvent(RunId, Sequence, OccurredAt);

public sealed record CollectionResult(
    CollectionRunId RunId,
    RunState State,
    int PagesDiscovered,
    int PagesCommitted,
    int PagesFailed,
    int ThreadsDiscovered,
    int ThreadsCommitted,
    int ThreadsFailed,
    ImmutableArray<CollectionIssue> Issues,
    bool CanResume);

public sealed record CollectionIssue(
    CollectionErrorCode Code,
    CollectionStage Stage,
    ErrorDisposition Disposition,
    string SafeMessage,
    Uri? Uri = null,
    ThreadKey? Thread = null)
{
  public static CollectionIssue Permanent(
      CollectionErrorCode code,
      CollectionStage stage,
      string safeMessage,
      Uri? uri = null,
      ThreadKey? thread = null) =>
      new(code, stage, ErrorDisposition.Permanent, safeMessage, uri, thread);

  public static CollectionIssue Resumable(
      CollectionErrorCode code,
      CollectionStage stage,
      string safeMessage,
      Uri? uri = null,
      ThreadKey? thread = null) =>
      new(code, stage, ErrorDisposition.Resumable, safeMessage, uri, thread);
}

public enum CollectionStage
{
  Open,
  Acquire,
  VerifySnapshot,
  DiscoverPagination,
  Parse,
  Commit,
  DownloadImage,
  Finalize
}

public enum ErrorDisposition
{
  NeedsUser,
  Retryable,
  Resumable,
  Permanent
}

public enum CollectionErrorCode
{
  EngineBusy,
  InvalidPlan,
  RunNotFound,
  RunNotRecoverable,
  AuthenticationRequired,
  HumanVerificationRequired,
  SnapshotRejected,
  PaginationIncomplete,
  NavigationFailed,
  ParseFailed,
  IdentityMismatch,
  PersistenceFailed,
  ImageRejected,
  Cancelled
}

public sealed class CollectionOpenException : Exception
{
  public CollectionOpenException(CollectionErrorCode code, string message, Exception? innerException = null)
      : base(message, innerException)
  {
    Code = code;
  }

  public CollectionErrorCode Code { get; }
}

public sealed record FavoritePageProjection(
    int? CurrentPage,
    int? DeclaredLastPage,
    ImmutableArray<Uri> DiscoveredPageUris,
    ImmutableArray<FavoriteReference> Threads,
    Uri? NextUri = null,
    bool HasExplicitTerminalEvidence = false);

public sealed record FavoriteReference(
    ThreadKey Key,
    Uri Uri,
    string? Title = null,
    string? RawFavoriteTime = null,
    DateTimeOffset? FavoriteTime = null,
    Uri? SourcePageUri = null);

public interface IFavoriteContextForumSourceAdapter
{
  ThreadProjection ParseThreadPage(PageSnapshot snapshot, FavoriteReference favorite);
}

public sealed record ThreadProjection(
    ThreadKey Key,
    string Title,
    Rfbm.Core.Models.BookmarkPost? CapturedPost = null);
