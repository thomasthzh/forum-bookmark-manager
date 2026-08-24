using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Rfbm.BrowserBridge.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PageKind>))]
public enum PageKind
{
  Unknown,
  FavoritesList,
  Thread
}

public sealed record PageSnapshot : IBrowserBridgeMessage
{
  public string Type { get; init; } = "page_snapshot";
  public required string Nonce { get; init; }
  public required PageKind PageKind { get; init; }
  public string? RunId { get; init; }
  public string? WorkItemId { get; init; }
  public string? Ticket { get; init; }
  public required Uri RequestedUrl { get; init; }
  public required Uri FinalUrl { get; init; }
  public required string Title { get; init; }
  public required string Html { get; init; }
  public required DateTimeOffset CapturedAtUtc { get; init; }
  public required int HtmlByteLength { get; init; }
  public required string HtmlSha256 { get; init; }

  public static PageSnapshot Create(
      string nonce,
      PageKind pageKind,
      string requestedUrl,
      string finalUrl,
      string title,
      string html,
      string? runId = null,
      string? workItemId = null,
      string? ticket = null,
      DateTimeOffset? capturedAtUtc = null)
  {
    var requestedUri = RequireHttpUri(requestedUrl, nameof(requestedUrl));
    var finalUri = RequireHttpUri(finalUrl, nameof(finalUrl));
    var normalizedHtml = html ?? throw new ArgumentNullException(nameof(html));

    return new PageSnapshot
    {
      Nonce = RequireText(nonce, nameof(nonce)),
      PageKind = pageKind,
      RunId = OptionalText(runId),
      WorkItemId = OptionalText(workItemId),
      Ticket = OptionalText(ticket),
      RequestedUrl = requestedUri,
      FinalUrl = finalUri,
      Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title.Trim(),
      Html = normalizedHtml,
      CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UtcNow,
      HtmlByteLength = Encoding.UTF8.GetByteCount(normalizedHtml),
      HtmlSha256 = ComputeSha256(normalizedHtml)
    };
  }

  public bool IsValid()
  {
    return Type == "page_snapshot" &&
           !string.IsNullOrWhiteSpace(Nonce) &&
           IsHttpUri(RequestedUrl) &&
           IsHttpUri(FinalUrl) &&
           Title is not null &&
           Html is not null &&
           CapturedAtUtc != default &&
           HtmlByteLength == Encoding.UTF8.GetByteCount(Html) &&
           string.Equals(HtmlSha256, ComputeSha256(Html), StringComparison.OrdinalIgnoreCase);
  }

  private static Uri RequireHttpUri(string value, string parameterName)
  {
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsHttpUri(uri))
    {
      throw new ArgumentException("Only absolute HTTP(S) URLs are accepted.", parameterName);
    }

    return uri;
  }

  private static bool IsHttpUri(Uri? uri)
  {
    return uri is { IsAbsoluteUri: true } &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
  }

  private static string RequireText(string value, string parameterName)
  {
    return string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A non-empty value is required.", parameterName)
        : value.Trim();
  }

  private static string? OptionalText(string? value)
  {
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
  }

  private static string ComputeSha256(string html)
  {
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
  }
}
