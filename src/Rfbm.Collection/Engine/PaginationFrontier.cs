using System.Text.RegularExpressions;

namespace Rfbm.Collection.Engine;

internal sealed partial class PaginationFrontier
{
  private readonly Queue<Uri> _pending = new();
  private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

  public int Count => _known.Count;
  public bool HasPending => _pending.Count > 0;

  public bool Add(Uri uri)
  {
    var key = LogicalKey(uri);
    if (!_known.Add(key))
    {
      return false;
    }

    _pending.Enqueue(uri);
    return true;
  }

  public bool Remember(Uri uri)
  {
    return _known.Add(LogicalKey(uri));
  }

  public Uri Take() => _pending.Dequeue();

  internal static string LogicalKey(Uri uri)
  {
    return $"page:{LogicalPageNumber(uri)}";
  }

  internal static int LogicalPageNumber(Uri uri)
  {
    var queryMatch = PageQueryRegex().Match(uri.Query);
    if (queryMatch.Success)
    {
      return ParsePageNumber(queryMatch.Groups[1].Value);
    }

    var pathMatch = PagePathRegex().Match(uri.AbsolutePath);
    return pathMatch.Success
        ? ParsePageNumber(pathMatch.Groups[1].Value)
        : 1;
  }

  private static int ParsePageNumber(string value) =>
      int.TryParse(
          value,
          System.Globalization.NumberStyles.None,
          System.Globalization.CultureInfo.InvariantCulture,
          out var page)
          ? page
          : int.MaxValue;

  internal static Uri WithPage(Uri source, int page)
  {
    if (page < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(page));
    }

    var builder = new UriBuilder(source);
    var parts = builder.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .ToList();
    var replaced = false;
    for (var index = 0; index < parts.Count; index++)
    {
      var separator = parts[index].IndexOf('=');
      var key = separator < 0 ? parts[index] : parts[index][..separator];
      if (!Uri.UnescapeDataString(key).Equals("page", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }
      parts[index] = $"{key}={page}";
      replaced = true;
      break;
    }
    if (!replaced)
    {
      parts.Add($"page={page}");
    }
    builder.Query = string.Join('&', parts);
    return builder.Uri;
  }

  [GeneratedRegex(@"(?:^|[?&])page=(\d+)(?:&|$)", RegexOptions.IgnoreCase)]
  private static partial Regex PageQueryRegex();

  [GeneratedRegex(@"(?:page[-_/]?)(\d+)", RegexOptions.IgnoreCase)]
  private static partial Regex PagePathRegex();
}
