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

  public static SnapshotClassification Classify(string? document)
  {
    var value = document ?? string.Empty;
    if (ChallengeStructure.IsMatch(value) || ChallengeTitle.IsMatch(value))
    {
      return SnapshotClassification.HumanVerificationRequired;
    }
    if (LoginStructure.IsMatch(value) || LoginTitle.IsMatch(value))
    {
      return SnapshotClassification.LoginRequired;
    }
    return SnapshotClassification.Content;
  }
}
