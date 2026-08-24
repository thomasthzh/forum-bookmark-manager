using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Rfbm.Core.Models;

namespace Rfbm.Core.Parsing;

public enum PageRejectionReason
{
  None,
  LoginRequired,
  Challenge,
  ErrorPage,
  IdentityMismatch,
  MissingContent
}

public sealed record PageValidationResult(
    bool IsValid,
    PageRejectionReason Reason,
    string Message)
{
  public static PageValidationResult Valid() => new(true, PageRejectionReason.None, string.Empty);

  public static PageValidationResult Rejected(PageRejectionReason reason, string message) =>
      new(false, reason, message);
}

public sealed class InvalidPageException : InvalidOperationException
{
  public InvalidPageException(PageValidationResult validation)
      : base(validation.Message)
  {
    Reason = validation.Reason;
  }

  public PageRejectionReason Reason { get; }
}

public static class PageValidation
{
  private static readonly string[] ChallengeMarkers =
  {
        "just a moment", "checking your browser", "cf-chl-", "cloudflare ray id",
        "人机验证", "人機驗證", "安全验证", "安全驗證", "captcha"
    };

  private static readonly string[] LoginMarkers =
  {
        "用户登录", "用戶登錄", "会员登录", "會員登錄", "请先登录", "請先登錄",
        "您需要先登录", "您需要先登錄"
    };

  private static readonly string[] ErrorMarkers =
  {
        "403 forbidden", "404 not found", "429 too many requests", "500 internal server error",
        "502 bad gateway", "503 service unavailable", "database error", "数据库错误", "數據庫錯誤",
        "指定的主题不存在", "指定的主題不存在", "页面不存在", "頁面不存在"
    };

  public static PageValidationResult ValidateThread(string html, Uri loadedUrl, ThreadKey expectedKey)
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(loadedUrl);

    var document = new HtmlParser().ParseDocument(html);
    return ValidateThread(document, loadedUrl, expectedKey, html);
  }

  internal static PageValidationResult ValidateThread(
      IDocument document,
      Uri loadedUrl,
      ThreadKey expectedKey,
      string fallbackText)
  {
    var generalRejection = ValidateGeneral(document, loadedUrl, fallbackText);
    if (!generalRejection.IsValid)
    {
      return generalRejection;
    }

    var loadedKey = ThreadKey.TryParse(loadedUrl.AbsoluteUri, expectedKey.SiteFamily, out var parsedLoaded)
        ? parsedLoaded
        : (ThreadKey?)null;
    if (loadedKey is not null && loadedKey.Value != expectedKey)
    {
      return PageValidationResult.Rejected(PageRejectionReason.IdentityMismatch, "The loaded URL identifies a different thread.");
    }

    var identityUrl = ExplicitIdentityUrl(document, loadedUrl);
    if (identityUrl is not null &&
        ThreadKey.TryParse(identityUrl.AbsoluteUri, expectedKey.SiteFamily, out var documentKey) &&
        documentKey != expectedKey)
    {
      return PageValidationResult.Rejected(PageRejectionReason.IdentityMismatch, "The page canonical identity belongs to a different thread.");
    }

    if (document.QuerySelector("#thread_subject, .ts, h1") is null ||
        document.QuerySelector("#postlist .pcb, [id^='postmessage_'], td.t_f, .t_f") is null)
    {
      return PageValidationResult.Rejected(PageRejectionReason.MissingContent, "The page has no recognizable Discuz thread content.");
    }

    return PageValidationResult.Valid();
  }

  public static PageValidationResult ValidateCollection(string html, Uri loadedUrl)
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(loadedUrl);
    var document = new HtmlParser().ParseDocument(html);
    return ValidateGeneral(document, loadedUrl, html);
  }

  internal static PageValidationResult ValidateCollection(IDocument document, Uri loadedUrl, string fallbackText) =>
      ValidateGeneral(document, loadedUrl, fallbackText);

  private static PageValidationResult ValidateGeneral(IDocument document, Uri loadedUrl, string fallbackText)
  {
    var normalizedText = CleanText(document.DocumentElement?.TextContent ?? fallbackText).ToLowerInvariant();
    var title = CleanText(document.Title ?? string.Empty).ToLowerInvariant();
    var combined = $"{title} {normalizedText}";

    if (ChallengeMarkers.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase)))
    {
      return PageValidationResult.Rejected(PageRejectionReason.Challenge, "The page is a browser/security challenge.");
    }

    if (IsLoginPage(document, loadedUrl, combined))
    {
      return PageValidationResult.Rejected(PageRejectionReason.LoginRequired, "The page requires an interactive login.");
    }

    if (ErrorMarkers.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase)))
    {
      return PageValidationResult.Rejected(PageRejectionReason.ErrorPage, "The page is an HTTP/forum error response.");
    }

    return PageValidationResult.Valid();
  }

  private static bool IsLoginPage(IDocument document, Uri loadedUrl, string combined)
  {
    if (document.QuerySelector("#loginform, form[action*='logging']") is not null)
    {
      return true;
    }

    if (document.QuerySelector("input[name='password']") is not null &&
        (loadedUrl.Query.Contains("logging", StringComparison.OrdinalIgnoreCase) ||
         loadedUrl.AbsolutePath.Contains("member", StringComparison.OrdinalIgnoreCase) ||
         LoginMarkers.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase))))
    {
      return true;
    }

    return LoginMarkers.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase));
  }

  private static Uri? ExplicitIdentityUrl(IDocument document, Uri loadedUrl)
  {
    var raw = document.QuerySelector("link[rel~='canonical']")?.GetAttribute("href")
        ?? document.QuerySelector("meta[property='og:url']")?.GetAttribute("content");
    if (string.IsNullOrWhiteSpace(raw))
    {
      return null;
    }

    return Uri.TryCreate(loadedUrl, raw, out var resolved) ? resolved : null;
  }

  private static string CleanText(string text) =>
      string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
