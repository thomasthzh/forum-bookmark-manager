using Rfbm.Collection.Contracts;

namespace Rfbm.Collection.Acquisition;

public interface IPageAcquisitionAdapter
{
  ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken);
}

public interface IInteractivePageAcquisitionAdapter
{
  ValueTask<bool> ResolveUserActionAsync(
      PageRequest request,
      PageNeedsUser prompt,
      CancellationToken cancellationToken);
}

public interface IForumSourceAdapter
{
  FavoritePageProjection ParseFavoritePage(PageSnapshot snapshot, SiteDefinition site);
  ThreadProjection ParseThreadPage(PageSnapshot snapshot, ThreadKey expectedThread);
}

public enum PageKind
{
  FavoriteIndex,
  ThreadDetail
}

public sealed record PageRequest(
    CollectionRunId RunId,
    PageKind Kind,
    Uri Uri,
    ThreadKey? ExpectedThread,
    int MaxSnapshotBytes);

public abstract record PageAcquisitionResult;

public sealed record PageCaptured(PageSnapshot Snapshot) : PageAcquisitionResult;

public sealed record PageNeedsUser(
    CollectionErrorCode Code,
    Uri Uri,
    string SafeMessage) : PageAcquisitionResult;

public sealed record PageUnavailable(CollectionIssue Issue) : PageAcquisitionResult;

public sealed record PageSnapshot(
    Uri RequestedUri,
    Uri FinalUri,
    string MediaType,
    string Document,
    DateTimeOffset CapturedAt,
    PageKind? ClaimedKind = null,
    string? Sha256 = null)
{
  public int Utf8Size => System.Text.Encoding.UTF8.GetByteCount(Document);

  public string ComputedSha256 => Convert.ToHexString(
      System.Security.Cryptography.SHA256.HashData(
          System.Text.Encoding.UTF8.GetBytes(Document)))
      .ToLowerInvariant();
}
