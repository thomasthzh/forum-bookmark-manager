using System.IO;
using System.Text.Json;
using Rfbm.BrowserBridge.Contracts;
using Rfbm.Collection.Adapters;
using Rfbm.Core.Parsing;
using Rfbm.Data.Storage;
using BridgePageKind = Rfbm.BrowserBridge.Contracts.PageKind;

namespace Rfbm.Native.Services;

public sealed record BridgeImportResult(int ImportedPosts, string SafeMessage);

public sealed class BridgeInboxProcessor
{
  private readonly PostRepository _repository;
  private readonly string _selectorDirectory;
  private readonly string _siteFamily;

  public BridgeInboxProcessor(
      PostRepository repository,
      string selectorDirectory,
      string siteFamily)
  {
    ArgumentNullException.ThrowIfNull(repository);
    ArgumentException.ThrowIfNullOrWhiteSpace(selectorDirectory);
    ArgumentException.ThrowIfNullOrWhiteSpace(siteFamily);
    _repository = repository;
    _selectorDirectory = Path.GetFullPath(selectorDirectory);
    _siteFamily = siteFamily.Trim().ToLowerInvariant();
  }

  public async ValueTask<BridgeImportResult> ProcessAsync(
      IBrowserBridgeMessage message,
      CancellationToken cancellationToken = default)
  {
    return message switch
    {
      PageSnapshot { PageKind: BridgePageKind.Thread } snapshot =>
          await ImportThreadAsync(snapshot, cancellationToken),
      PageSnapshot { PageKind: BridgePageKind.FavoritesList } snapshot =>
          await ImportFavoritesAsync(snapshot, cancellationToken),
      PageSnapshot => throw new InvalidDataException("扩展无法判断当前页面类型。"),
      SelectorProfileMessage profile =>
          await SaveSelectorProfileAsync(profile, cancellationToken),
      _ => throw new InvalidDataException("扩展消息类型不受支持。")
    };
  }

  private async ValueTask<BridgeImportResult> ImportThreadAsync(
      PageSnapshot snapshot,
      CancellationToken cancellationToken)
  {
    var post = DiscuzPageParser.ParseThread(
        snapshot.Html,
        snapshot.RequestedUrl,
        _siteFamily,
        contentBaseUrl: snapshot.FinalUrl);
    await _repository.UpsertCaptureAsync(CorePostCaptureMapper.Map(post), cancellationToken);
    return new BridgeImportResult(1, $"已从扩展导入“{post.Title}”。");
  }

  private async ValueTask<BridgeImportResult> ImportFavoritesAsync(
      PageSnapshot snapshot,
      CancellationToken cancellationToken)
  {
    var page = DiscuzPageParser.ParseFavorites(snapshot.Html, snapshot.FinalUrl, _siteFamily);
    foreach (var favorite in page.Items)
    {
      await _repository.UpsertCaptureAsync(new PostCapture
      {
        Key = new PostKey(favorite.Key.SiteFamily, favorite.Key.ThreadId),
        CanonicalUrl = favorite.Url,
        RawUrl = Observation<string?>.Observed(favorite.Url),
        FavoriteUrl = Observation<string?>.Observed(favorite.SourcePageUrl),
        Title = Observation<string>.Observed(favorite.Title),
        FavoriteTimeRaw = Observation<string?>.Observed(favorite.RawFavoriteTime),
        FavoriteTimeUtc = Observation<DateTimeOffset?>.Observed(favorite.FavoriteTime)
      }, cancellationToken);
    }

    return new BridgeImportResult(
        page.Items.Count,
        $"已从扩展导入 {page.Items.Count} 条收藏索引。详情可由批量采集补齐。");
  }

  private async ValueTask<BridgeImportResult> SaveSelectorProfileAsync(
      SelectorProfileMessage profile,
      CancellationToken cancellationToken)
  {
    Directory.CreateDirectory(_selectorDirectory);
    var safeSiteKey = string.Concat(profile.SiteKey.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
            ? character
            : '_'));
    safeSiteKey = safeSiteKey.Length == 0 ? "site" : safeSiteKey[..Math.Min(safeSiteKey.Length, 100)];
    var targetPath = Path.Combine(_selectorDirectory, safeSiteKey + ".json");
    var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      var json = JsonSerializer.Serialize(new
      {
        siteKey = profile.SiteKey,
        sourceUrl = profile.SourceUrl,
        selectors = profile.Selectors,
        capturedAtUtc = profile.CapturedAtUtc
      }, BridgeJson.Options);
      await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
      File.Move(temporaryPath, targetPath, overwrite: true);
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }

    return new BridgeImportResult(0, $"已保存 {profile.Selectors.Count} 个页面选择器。");
  }
}
