using System.Text.RegularExpressions;

namespace Rfbm.Collection.Playwright;

public enum SnapshotClassification
{
  Content,
  LoginRequired,
  HumanVerificationRequired
}

public static class PlaywrightSnapshotClassifier
{
  private static readonly Regex ChallengeStructure = new(
      "<(?:form|div|section|iframe|input)[^>]*(?:id|class|name|src|action)\\s*=\\s*['\"][^'\"]*(?:captcha|geetest|hcaptcha|turnstile)[^'\"]*['\"]",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

  private static readonly Regex ChallengeTitle = new(
      "<title[^>]*>\\s*(?:安全验证|人机验证|验证码)(?:\\s*[-–—|].*)?\\s*</title>",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

  private static readonly Regex LoginStructure = new(
      "(?:<form[^>]*(?:id|name|class|action)\\s*=\\s*['\"][^'\"]*(?:loginform|member\\.php[^'\"]*logging|(?:^|[-_])login(?:[-_]|$))[^'\"]*['\"]|<input[^>]*name\\s*=\\s*['\"]loginfield['\"])",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

  private static readonly Regex LoginTitle = new(
      "<title[^>]*>\\s*(?:用户登录|会员登录)(?:\\s*[-–—|].*)?\\s*</title>",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

  // Discuz may answer an authenticated-only route with a generic "提示信息"
  // page instead of rendering the login form. Keep the marker paired with the
  // title or redirect so ordinary discussion text cannot be mistaken for a
  // session boundary.
  private static readonly Regex LoginPrompt = new(
      "请先登录后才能继续浏览|請先登錄後才能繼續瀏覽|您需要登录|您需要登錄",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

  private static readonly Regex LoginPromptTitle = new(
      "<title[^>]*>\\s*提示信息[^<]*</title>",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

  private static readonly Regex LoginRedirect = new(
      "(?:member\\.php|login(?:\\.php)?)[^<\\\"']*(?:mod=logging|action=login|login)",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

  public static SnapshotClassification Classify(string? document)
  {
    var value = document ?? string.Empty;
    if (ChallengeStructure.IsMatch(value) || ChallengeTitle.IsMatch(value))
    {
      return SnapshotClassification.HumanVerificationRequired;
    }
    if (LoginStructure.IsMatch(value) ||
        LoginTitle.IsMatch(value) ||
        (LoginPrompt.IsMatch(value) &&
         (LoginPromptTitle.IsMatch(value) || LoginRedirect.IsMatch(value))))
    {
      return SnapshotClassification.LoginRequired;
    }
    return SnapshotClassification.Content;
  }
}
