namespace Rfbm.BrowserBridge.Contracts;

public sealed record SelectorProfileMessage : IBrowserBridgeMessage
{
  public static readonly IReadOnlySet<string> SupportedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "favoritesList",
        "favoriteItem",
        "pagination",
        "threadTitle",
        "postBody",
        "images",
        "downloadLinks",
        "password",
        "counters",
        "favoriteTime"
    };

  public string Type { get; init; } = "selector_profile";
  public required string Nonce { get; init; }
  public required string SiteKey { get; init; }
  public required Uri SourceUrl { get; init; }
  public required Dictionary<string, string> Selectors { get; init; }
  public string? Ticket { get; init; }
  public required DateTimeOffset CapturedAtUtc { get; init; }

  public static SelectorProfileMessage Create(
      string nonce,
      string siteKey,
      string sourceUrl,
      IReadOnlyDictionary<string, string> selectors,
      string? ticket = null,
      DateTimeOffset? capturedAtUtc = null)
  {
    if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) ||
        (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
    {
      throw new ArgumentException("Only absolute HTTP(S) URLs are accepted.", nameof(sourceUrl));
    }

    return new SelectorProfileMessage
    {
      Nonce = RequireText(nonce, nameof(nonce)),
      SiteKey = RequireText(siteKey, nameof(siteKey)),
      SourceUrl = sourceUri,
      Selectors = NormalizeSelectors(selectors),
      Ticket = string.IsNullOrWhiteSpace(ticket) ? null : ticket.Trim(),
      CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UtcNow
    };
  }

  public bool IsValid()
  {
    if (Type != "selector_profile" ||
        string.IsNullOrWhiteSpace(Nonce) ||
        string.IsNullOrWhiteSpace(SiteKey) ||
        SourceUrl is not { IsAbsoluteUri: true } ||
        (SourceUrl.Scheme != Uri.UriSchemeHttp && SourceUrl.Scheme != Uri.UriSchemeHttps) ||
        CapturedAtUtc == default ||
        Selectors is null || Selectors.Count == 0)
    {
      return false;
    }

    return Selectors.All(pair =>
        SupportedFields.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));
  }

  private static Dictionary<string, string> NormalizeSelectors(IReadOnlyDictionary<string, string> selectors)
  {
    ArgumentNullException.ThrowIfNull(selectors);
    if (selectors.Count == 0)
    {
      throw new ArgumentException("At least one selector is required.", nameof(selectors));
    }

    var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var pair in selectors)
    {
      if (!SupportedFields.Contains(pair.Key))
      {
        throw new ArgumentException($"Unsupported selector field: {pair.Key}", nameof(selectors));
      }

      normalized[pair.Key] = RequireText(pair.Value, nameof(selectors));
    }

    return normalized;
  }

  private static string RequireText(string value, string parameterName)
  {
    return string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A non-empty value is required.", parameterName)
        : value.Trim();
  }
}
