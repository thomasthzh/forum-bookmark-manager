using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;

namespace Rfbm.Native;

public sealed record CollectionSetupResult(
    Uri FavoritesUrl,
    string SiteFamily,
    ImmutableArray<Uri> MirrorOrigins,
    int ThreadConcurrency,
    bool VerifyFirst,
    MihomoSetup? Mihomo);

public sealed record MihomoSetup(Uri Controller, string Group, string Proxy, string? Secret)
{
  public static bool TryCreate(
      string controllerText,
      string group,
      string proxy,
      string? secret,
      out MihomoSetup? setup)
  {
    setup = null;
    if (!Uri.TryCreate(controllerText.Trim(), UriKind.Absolute, out var controller) ||
        controller.Scheme is not ("http" or "https") ||
        !IPAddress.TryParse(controller.Host, out var address) ||
        !IPAddress.IsLoopback(address) ||
        string.IsNullOrWhiteSpace(group) ||
        string.IsNullOrWhiteSpace(proxy))
    {
      return false;
    }

    setup = new MihomoSetup(
        new Uri(controller.GetLeftPart(UriPartial.Authority)),
        group.Trim(),
        proxy.Trim(),
        string.IsNullOrWhiteSpace(secret) ? null : secret);
    return true;
  }
}

public partial class CollectionSetupWindow : Window
{
  public CollectionSetupWindow()
  {
    InitializeComponent();
    Loaded += (_, _) => FavoritesUrlBox.Focus();
  }

  public CollectionSetupResult? Result { get; private set; }

  private void Start_Click(object sender, RoutedEventArgs e)
  {
    ErrorText.Text = string.Empty;
    if (!Uri.TryCreate(FavoritesUrlBox.Text.Trim(), UriKind.Absolute, out var favoritesUrl) ||
        favoritesUrl.Scheme is not ("http" or "https"))
    {
      ErrorText.Text = "请输入有效的 HTTP(S) 收藏页 URL。";
      return;
    }

    var family = SiteFamilyBox.Text.Trim();
    if (family.Length == 0)
    {
      family = favoritesUrl.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
          ? favoritesUrl.Host[4..]
          : favoritesUrl.Host;
      family = family.ToLowerInvariant();
      SiteFamilyBox.Text = family;
    }
    if (!Regex.IsMatch(family, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$"))
    {
      ErrorText.Text = "站点家族标识只能包含字母、数字、点、下划线和连字符。";
      return;
    }

    if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var concurrency) || concurrency is < 1 or > 12)
    {
      ErrorText.Text = "帖子并发必须是 1–12 之间的整数。";
      return;
    }

    var mirrors = ImmutableArray.CreateBuilder<Uri>();
    foreach (var line in MirrorsBox.Text.Split(
                 ['\r', '\n'],
                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      if (!Uri.TryCreate(line, UriKind.Absolute, out var mirror) ||
          mirror.Scheme is not ("http" or "https"))
      {
        ErrorText.Text = $"镜像 origin 无效：{line}";
        return;
      }
      mirrors.Add(new Uri(mirror.GetLeftPart(UriPartial.Authority)));
    }

    MihomoSetup? mihomo = null;
    if (EnableMihomoCheckBox.IsChecked == true && !MihomoSetup.TryCreate(
            MihomoControllerBox.Text,
            MihomoGroupBox.Text,
            MihomoProxyBox.Text,
            MihomoSecretBox.Password,
            out mihomo))
    {
      ErrorText.Text = "Mihomo 必须使用本机回环 IP 控制器，并填写策略组和目标线路。";
      return;
    }

    Result = new CollectionSetupResult(
        favoritesUrl,
        family.ToLowerInvariant(),
        mirrors.Distinct().ToImmutableArray(),
        concurrency,
        VerifyFirstCheckBox.IsChecked == true,
        mihomo);
    DialogResult = true;
  }

  private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
