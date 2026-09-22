using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Rfbm.Core.Models;

namespace Rfbm.Core.Parsing;

/// <summary>
/// Parses validated Discuz collection/detail snapshots into normalized domain
/// values. Acquisition adapters do not need to understand DOM quirks.
/// </summary>
public static partial class DiscuzPageParser
{
  private static readonly string[] ImageSourceAttributes =
  {
        "file", "zoomfile", "data-original", "data-original-src", "data-src",
        "data-echo", "data-lazy-src", "lazy-src", "src"
    };

  private static readonly string[] ImageExclusionMarkers =
  {
        "avatar", "smiley", "static/", "common/", "logo", "icon", "emotion"
    };

  private static readonly string[] PasswordLabels =
  {
        "解压密码", "解压码", "解壓密碼", "解壓碼", "压缩密码", "壓縮密碼",
        "password", "pass", "密码", "密碼"
    };

  private static readonly string[] AccessCodeLabels =
  {
        "提取码", "提取碼", "访问码", "訪問碼", "access code"
    };

  public static FavoritePage ParseFavorites(string html, Uri pageUrl, string siteFamily)
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(pageUrl);
    ArgumentException.ThrowIfNullOrWhiteSpace(siteFamily);

    var document = new HtmlParser().ParseDocument(html);
    var validation = PageValidation.ValidateCollection(document, pageUrl, html);
    if (!validation.IsValid)
    {
      throw new InvalidPageException(validation);
    }

    var pagination = ParsePagination(document, pageUrl);
    var pageText = CleanText(document.DocumentElement?.TextContent ?? string.Empty);
    if (pageText.Contains("您还没有添加任何收藏", StringComparison.Ordinal) ||
        pageText.Contains("您還沒有添加任何收藏", StringComparison.Ordinal))
    {
      return new FavoritePage(Array.Empty<FavoriteReference>(), pagination);
    }

    var containers = FavoriteContainers(document);
    var seen = new HashSet<ThreadKey>();
    var favorites = new List<FavoriteReference>();
    foreach (var container in containers)
    {
      foreach (var anchor in container.QuerySelectorAll("a[href]"))
      {
        var href = anchor.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(pageUrl, href, out var absolute))
        {
          continue;
        }

        if (!ThreadKey.TryParse(absolute.AbsoluteUri, siteFamily, out var key) || !seen.Add(key))
        {
          continue;
        }

        var row = anchor.Closest("li, tr, .fav-row, .favorite-row, div") ?? container;
        var rawFavoriteTime = FindFavoriteTime(CleanText(row.TextContent));
        favorites.Add(new FavoriteReference(
            key,
            absolute.AbsoluteUri,
            CleanText(anchor.TextContent),
            rawFavoriteTime,
            NormalizeFavoriteTime(rawFavoriteTime),
            pageUrl.AbsoluteUri));
      }
    }

    return new FavoritePage(favorites, pagination);
  }

  public static BookmarkPost ParseThread(
      string html,
      Uri postUrl,
      string siteFamily,
      FavoriteReference? favorite = null,
      Uri? contentBaseUrl = null)
  {
    ArgumentNullException.ThrowIfNull(html);
    ArgumentNullException.ThrowIfNull(postUrl);
    ArgumentException.ThrowIfNullOrWhiteSpace(siteFamily);

    var key = ThreadKey.Parse(postUrl.AbsoluteUri, siteFamily);
    if (favorite is not null && favorite.Key != key)
    {
      throw new ArgumentException("Favorite reference belongs to a different thread.", nameof(favorite));
    }

    var loadedUrl = contentBaseUrl ?? postUrl;
    var document = new HtmlParser().ParseDocument(html);
    var validation = PageValidation.ValidateThread(document, loadedUrl, key, html);
    if (!validation.IsValid)
    {
      throw new InvalidPageException(validation);
    }

    var firstFloor = FirstFloor(document);
    var body = FirstFloorBody(firstFloor) ?? firstFloor;
    var title = ExtractTitle(document);
    var bodyText = ObserveElementText(body);
    var pageText = CleanText(document.DocumentElement?.TextContent ?? string.Empty);

    return new BookmarkPost(
        key.ToString(),
        postUrl.AbsoluteUri,
        title,
        DetectProjectType(document, title, bodyText.ValueOrDefault ?? string.Empty),
        PostStatus.NotDownloaded,
        favorite?.FavoriteTime)
    {
      Key = key,
      CanonicalUrl = postUrl.AbsoluteUri,
      ContentUrl = loadedUrl.AbsoluteUri,
      FavoriteSourceUrl = favorite?.SourcePageUrl,
      RawFavoriteTime = favorite?.RawFavoriteTime,
      VisitCount = ObserveCount(pageText, "查看", "访问", "訪問", "views"),
      DownloadCount = ObserveCount(pageText, "下载", "下載", "downloads"),
      FavoriteCount = ObserveCount(pageText, "收藏", "favorites"),
      ExtractPassword = ObserveSecret(firstFloor ?? body, PasswordLabels, ArchivePasswordRegex()),
      AccessCode = ObserveSecret(firstFloor ?? body, AccessCodeLabels, AccessCodeRegex()),
      BodyText = bodyText,
      Images = ExtractImages(firstFloor ?? body, loadedUrl, 9),
      ResourceLinks = ExtractResourceLinks(firstFloor ?? body, loadedUrl)
    };
  }

  public static string? ExtractPassword(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    var match = ArchivePasswordRegex().Match(text);
    return match.Success ? match.Groups[1].Value.Trim() : null;
  }

  private static IReadOnlyList<IElement> FavoriteContainers(IDocument document)
  {
    var scoped = document.QuerySelectorAll(
            "[id^='favorite_li_'], [id^='favorite_'], .el li, .xld li, #favorite_ul li, .fav-row, .favorite-row")
        .Distinct()
        .ToArray();
    if (scoped.Length > 0)
    {
      return scoped;
    }

    var content = document.QuerySelector("#ct, .ct2_a, .mn");
    return content is null ? Array.Empty<IElement>() : new[] { content };
  }

  private static PaginationEvidence ParsePagination(IDocument document, Uri pageUrl)
  {
    var currentPage = PageNumber(pageUrl.AbsoluteUri) ?? 1;
    var pageNumbers = new HashSet<int> { currentPage };
    var pager = document.QuerySelector(".pg");
    if (pager is not null)
    {
      foreach (var node in pager.QuerySelectorAll("a[href], strong"))
      {
        var number = node.LocalName.Equals("strong", StringComparison.OrdinalIgnoreCase)
            ? ParsePositiveInt(CleanText(node.TextContent))
            : PageNumber(node.GetAttribute("href"));
        if (number is > 0)
        {
          pageNumbers.Add(number.Value);
        }
      }
    }

    var maxPage = pageNumbers.Max();
    var urls = pager is null
        ? Array.Empty<string>()
        : Enumerable.Range(1, maxPage).Select(page => ReplacePage(pageUrl, page)).ToArray();
    var nextHref = document.QuerySelector("a.nxt[href]")?.GetAttribute("href");
    var nextUrl = nextHref is not null && Uri.TryCreate(pageUrl, nextHref, out var resolved)
        ? resolved.AbsoluteUri
        : null;
    return new PaginationEvidence(currentPage, maxPage, urls, nextUrl);
  }

  private static IElement? FirstFloor(IDocument document)
  {
    foreach (var selector in new[]
             {
                     "#postlist > div[id^='post_'] .pcb",
                     "#postlist table[id^='pid'] .pcb",
                     "div[id^='post_'] .pcb",
                     ".pcb"
                 })
    {
      var element = document.QuerySelector(selector);
      if (element is not null)
      {
        return element;
      }
    }

    return document.QuerySelector("td.t_f, .t_f, [id^='postmessage_']");
  }

  private static IElement? FirstFloorBody(IElement? firstFloor) =>
      firstFloor?.QuerySelector("[id^='postmessage_'], td.t_f, .t_f, .t_fsz") ?? firstFloor;

  private static string ExtractTitle(IDocument document)
  {
    var subject = CleanText(document.QuerySelector("#thread_subject")?.TextContent ?? string.Empty);
    if (subject.Length > 0)
    {
      // Discuz X3.4 themes commonly keep the board/category prefix in an
      // adjacent h1.ts link and the actual subject in #thread_subject.
      var prefix = CleanText(document.QuerySelector("h1.ts > a, h1.ts a, .ts > a")?.TextContent ?? string.Empty);
      return prefix.Length > 0 && !subject.StartsWith(prefix, StringComparison.Ordinal)
          ? $"{prefix} {subject}"
          : subject;
    }

    foreach (var selector in new[] { "h1", ".ts span", ".ts" })
    {
      var title = CleanText(document.QuerySelector(selector)?.TextContent ?? string.Empty);
      if (title.Length > 0)
      {
        return title;
      }
    }

    var documentTitle = CleanText(document.Title ?? string.Empty);
    var separator = documentTitle.IndexOf(" - ", StringComparison.Ordinal);
    return separator > 0 ? documentTitle[..separator].Trim() : documentTitle;
  }

  private static string DetectProjectType(IDocument document, string title, string bodyText)
  {
    var breadcrumbs = document.QuerySelectorAll("#pt a, .z a, h1.ts > a, h1.ts a, .deanjstop h3 a")
        .Select(anchor => CleanText(anchor.TextContent))
        .Where(text => text.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    foreach (var label in breadcrumbs.Concat(new[] { title }))
    {
      var bracket = BracketedTypeRegex().Match(label);
      if (bracket.Success)
      {
        return bracket.Groups[1].Value.Trim();
      }
    }

    foreach (var text in breadcrumbs.Concat(new[] { title, bodyText }))
    {
      if (text.Contains("游戏", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("game", StringComparison.OrdinalIgnoreCase))
      {
        return "游戏";
      }

      if (text.Contains("国产自拍", StringComparison.Ordinal) ||
          (text.Contains("自拍", StringComparison.Ordinal) && text.Contains("国产", StringComparison.Ordinal)))
      {
        return "国产自拍";
      }
    }

    var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "论坛", "首頁", "首页", "资源", "資源", "板块", "板塊", "全部", "未分类", "導讀", "导读"
        };
    return breadcrumbs.Reverse().FirstOrDefault(value => !generic.Contains(value) && value.Length <= 24) ?? "未分类";
  }

  private static Observed<int> ObserveCount(string text, params string[] labels)
  {
    var labelExpression = string.Join('|', labels.Select(Regex.Escape));
    var regex = new Regex(
        $@"(?:{labelExpression})\s*(?:次数|次數)?\s*[:：]?\s*(?<number>\d[\d,.]*)\s*(?<suffix>[万萬亿億千kKmMwW])?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    var match = regex.Match(text);
    if (match.Success && TryParseCount(match, out var value))
    {
      return Observed<int>.From(value);
    }

    return labels.Any(label => text.Contains(label, StringComparison.OrdinalIgnoreCase))
        ? Observed<int>.Empty()
        : Observed<int>.Unobservable();
  }

  private static bool TryParseCount(Match match, out int value)
  {
    value = 0;
    var numberText = match.Groups["number"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
    if (!decimal.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
    {
      return false;
    }

    var multiplier = match.Groups["suffix"].Value.ToLowerInvariant() switch
    {
      "万" or "萬" or "w" => 10_000m,
      "亿" or "億" => 100_000_000m,
      "千" or "k" => 1_000m,
      "m" => 1_000_000m,
      _ => 1m
    };
    var scaled = decimal.Round(number * multiplier, 0, MidpointRounding.AwayFromZero);
    if (scaled < 0 || scaled > int.MaxValue)
    {
      return false;
    }

    value = (int)scaled;
    return true;
  }

  private static Observed<string> ObserveSecret(
      IElement? container,
      IReadOnlyCollection<string> labels,
      Regex pattern)
  {
    if (container is null)
    {
      return Observed<string>.Unobservable();
    }

    foreach (var row in container.QuerySelectorAll("tr"))
    {
      var heading = CleanText(row.QuerySelector("th, dt")?.TextContent ?? string.Empty).TrimEnd(':', '：');
      if (!labels.Any(label => heading.Contains(label, StringComparison.OrdinalIgnoreCase)))
      {
        continue;
      }

      var value = CleanText(row.QuerySelector("td, dd")?.TextContent ?? string.Empty);
      return value.Length > 0 ? Observed<string>.From(value) : Observed<string>.Empty();
    }

    var text = CleanText(container.TextContent);
    var match = pattern.Match(text);
    if (match.Success)
    {
      return Observed<string>.From(match.Groups[1].Value.Trim());
    }

    return labels.Any(label => text.Contains(label, StringComparison.OrdinalIgnoreCase))
        ? Observed<string>.Empty()
        : Observed<string>.Unobservable();
  }

  private static Observed<string> ObserveElementText(IElement? element)
  {
    if (element is null)
    {
      return Observed<string>.Unobservable();
    }

    var text = CleanText(element.TextContent);
    return text.Length == 0 ? Observed<string>.Empty() : Observed<string>.From(text);
  }

  private static IReadOnlyList<PostImage> ExtractImages(IElement? container, Uri baseUrl, int limit)
  {
    if (container is null)
    {
      return Array.Empty<PostImage>();
    }

    var images = new List<PostImage>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var image in container.QuerySelectorAll("img"))
    {
      foreach (var source in ImageCandidates(image))
      {
        if (IsExcludedImage(image, source) || !ResolveHttpUrl(baseUrl, source, out var absolute) || !seen.Add(absolute))
        {
          continue;
        }

        images.Add(new PostImage(
            absolute,
            images.Count + 1,
            AltText: CleanText(image.GetAttribute("alt") ?? string.Empty) is { Length: > 0 } alt ? alt : null));
        break;
      }

      if (images.Count >= limit)
      {
        break;
      }
    }

    return images;
  }

  private static IEnumerable<string> ImageCandidates(IElement image)
  {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var attribute in ImageSourceAttributes)
    {
      var value = image.GetAttribute(attribute)?.Trim();
      if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
      {
        yield return value;
      }
    }

    foreach (var attribute in new[] { "srcset", "data-srcset" })
    {
      var srcSet = image.GetAttribute(attribute);
      if (string.IsNullOrWhiteSpace(srcSet))
      {
        continue;
      }

      foreach (var candidate in srcSet.Split(','))
      {
        var value = candidate.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (value.Length > 0 && seen.Add(value))
        {
          yield return value;
        }
      }
    }
  }

  private static bool IsExcludedImage(IElement image, string source)
  {
    if (ImageExclusionMarkers.Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase)))
    {
      return true;
    }

    var width = ParsePositiveInt(image.GetAttribute("width"));
    var height = ParsePositiveInt(image.GetAttribute("height"));
    return width is not null && height is not null && (width < 80 || height < 80);
  }

  private static IReadOnlyList<ResourceLink> ExtractResourceLinks(IElement? container, Uri baseUrl)
  {
    if (container is null)
    {
      return Array.Empty<ResourceLink>();
    }

    var links = new List<ResourceLink>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var anchor in container.QuerySelectorAll("a[href]"))
    {
      var href = anchor.GetAttribute("href")?.Trim();
      if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') ||
          href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
          href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
          !ResolveResourceUrl(baseUrl, href, out var absolute) || !seen.Add(absolute))
      {
        continue;
      }

      links.Add(new ResourceLink(
          absolute,
          CleanText(anchor.TextContent),
          LinkContext(anchor, container)));
    }

    return links;
  }

  private static string LinkContext(IElement anchor, IElement boundary)
  {
    for (var node = anchor.ParentElement; node is not null; node = node.ParentElement)
    {
      if (node.LocalName is "p" or "li" or "tr" or "div")
      {
        var text = CleanText(node.TextContent);
        if (text.Length > 0)
        {
          return text;
        }
      }

      if (node == boundary)
      {
        break;
      }
    }

    return CleanText(anchor.TextContent);
  }

  private static bool ResolveHttpUrl(Uri baseUrl, string value, out string absolute)
  {
    absolute = string.Empty;
    if (!Uri.TryCreate(baseUrl, value, out var resolved) ||
        (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
    {
      return false;
    }

    absolute = resolved.AbsoluteUri;
    return true;
  }

  private static bool ResolveResourceUrl(Uri baseUrl, string value, out string absolute)
  {
    absolute = string.Empty;
    if (!Uri.TryCreate(baseUrl, value, out var resolved) ||
        (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps &&
         !resolved.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) &&
         !resolved.Scheme.Equals("ed2k", StringComparison.OrdinalIgnoreCase)))
    {
      return false;
    }

    absolute = resolved.AbsoluteUri;
    return true;
  }

  private static string? FindFavoriteTime(string text)
  {
    var exact = FavoriteDateRegex().Match(text);
    if (exact.Success)
    {
      return exact.Value;
    }

    var relative = RelativeTimeRegex().Match(text);
    return relative.Success ? relative.Value : null;
  }

  private static DateTimeOffset? NormalizeFavoriteTime(string? raw)
  {
    if (raw is null || !DateTime.TryParseExact(
            raw,
            new[] { "yyyy-M-d H:mm", "yyyy-MM-dd H:mm", "yyyy-M-d", "yyyy-MM-dd" },
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed))
    {
      return null;
    }

    parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
  }

  private static int? PageNumber(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var match = PageQueryRegex().Match(value);
    return match.Success && int.TryParse(match.Groups[1].Value, out var page) ? page : null;
  }

  private static string ReplacePage(Uri url, int page)
  {
    var builder = new UriBuilder(url);
    var parts = builder.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .ToList();
    var replaced = false;
    for (var index = 0; index < parts.Count; index++)
    {
      var separator = parts[index].IndexOf('=');
      var key = separator >= 0 ? parts[index][..separator] : parts[index];
      if (!Uri.UnescapeDataString(key).Equals("page", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      parts[index] = $"{key}={page.ToString(CultureInfo.InvariantCulture)}";
      replaced = true;
      break;
    }

    if (!replaced)
    {
      parts.Add($"page={page.ToString(CultureInfo.InvariantCulture)}");
    }

    builder.Query = string.Join('&', parts);
    return builder.Uri.AbsoluteUri;
  }

  private static int? ParsePositiveInt(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var match = NumberRegex().Match(value);
    return match.Success && int.TryParse(match.Value.Replace(",", string.Empty, StringComparison.Ordinal), out var number)
        ? number
        : null;
  }

  private static string CleanText(string text) =>
      string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

  [GeneratedRegex(@"20\d{2}-\d{1,2}-\d{1,2}(?:\s+\d{1,2}:\d{2})?", RegexOptions.CultureInvariant)]
  private static partial Regex FavoriteDateRegex();

  [GeneratedRegex(@"(?:刚刚|剛剛|半小时前|半小時前|\d+\s*(?:秒|分钟|分鐘|小时|小時|天|周|个月|個月|年)前|昨天(?:\s+\d{1,2}:\d{2})?|前天(?:\s+\d{1,2}:\d{2})?)", RegexOptions.CultureInvariant)]
  private static partial Regex RelativeTimeRegex();

  [GeneratedRegex(@"(?:^|[?&])page=(\d+)(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex PageQueryRegex();

  [GeneratedRegex(@"\d[\d,]*", RegexOptions.CultureInvariant)]
  private static partial Regex NumberRegex();

  [GeneratedRegex(@"(?:解压密码|解压码|解壓密碼|解壓碼|压缩密码|壓縮密碼|password|pass|(?<!取)密码|(?<!取)密碼)\s*[:：=]\s*([^\s，,。；;|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex ArchivePasswordRegex();

  [GeneratedRegex(@"(?:提取码|提取碼|访问码|訪問碼|access\s*code)\s*[:：=]?\s*([^\s，,。；;|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex AccessCodeRegex();

  [GeneratedRegex(@"^[\s\[【(（]+([^\]】)）]+)[\s\]】)）]+", RegexOptions.CultureInvariant)]
  private static partial Regex BracketedTypeRegex();
}
