using System.Globalization;
using System.Text.RegularExpressions;

namespace Rfbm.Core.Models;

/// <summary>
/// Host-independent Discuz thread identity. Mirror adapters intentionally pass
/// the same site family so query and pretty URLs on different hosts converge.
/// </summary>
public readonly partial record struct ThreadKey(string SiteFamily, string ThreadId)
{
  public static ThreadKey Parse(string url, string? siteFamily = null)
  {
    if (!TryParse(url, siteFamily, out var key))
    {
      throw new FormatException($"URL does not contain a Discuz thread id: {url}");
    }

    return key;
  }

  public static bool TryParse(string? url, string? siteFamily, out ThreadKey key)
  {
    key = default;
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
      return false;
    }

    var threadId = ThreadIdFromQuery(uri.Query) ?? ThreadIdFromPath(uri.AbsolutePath);
    if (threadId is null)
    {
      return false;
    }

    var family = NormalizeFamily(siteFamily ?? uri.Host);
    if (family.Length == 0)
    {
      return false;
    }

    key = new ThreadKey(family, NormalizeThreadId(threadId));
    return true;
  }

  public static bool TryParse(string? url, out ThreadKey key) => TryParse(url, null, out key);

  public override string ToString() => $"{SiteFamily}:{ThreadId}";

  private static string? ThreadIdFromQuery(string query)
  {
    var match = TidQueryRegex().Match(query);
    return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
  }

  private static string? ThreadIdFromPath(string path)
  {
    var match = PrettyThreadRegex().Match(path);
    return match.Success ? match.Groups[1].Value : null;
  }

  private static string NormalizeFamily(string family)
  {
    var normalized = family.Trim().TrimEnd('.').ToLowerInvariant();
    return normalized.StartsWith("www.", StringComparison.Ordinal)
        ? normalized[4..]
        : normalized;
  }

  private static string NormalizeThreadId(string threadId)
  {
    if (ulong.TryParse(threadId, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
    {
      return numeric.ToString(CultureInfo.InvariantCulture);
    }

    return threadId.Trim().ToLowerInvariant();
  }

  [GeneratedRegex(@"(?:^|[?&])tid=(\d+)(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex TidQueryRegex();

  [GeneratedRegex(@"(?:^|/)(?:thread|tid)-(\d+)(?:-|\.|/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex PrettyThreadRegex();
}
