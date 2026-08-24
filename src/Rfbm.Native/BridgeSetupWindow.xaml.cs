using System.Text.RegularExpressions;
using System.Windows;
using Rfbm.Native.Services;

namespace Rfbm.Native;

public sealed record BridgeSetupResult(string ExtensionId, string SiteFamily)
{
  public string AllowedOrigin => $"chrome-extension://{ExtensionId}";

  public static bool TryCreate(
      string extensionId,
      string siteFamily,
      out BridgeSetupResult? result)
  {
    result = null;
    var normalizedExtensionId = extensionId.Trim().ToLowerInvariant();
    var normalizedSiteFamily = siteFamily.Trim().ToLowerInvariant();
    if (!Regex.IsMatch(normalizedExtensionId, "^[a-p]{32}$") ||
        !Regex.IsMatch(normalizedSiteFamily, "^[a-z0-9][a-z0-9._-]{0,63}$"))
    {
      return false;
    }

    result = new BridgeSetupResult(normalizedExtensionId, normalizedSiteFamily);
    return true;
  }
}

public partial class BridgeSetupWindow : Window
{
  public BridgeSetupWindow()
  {
    InitializeComponent();
    ExtensionPathBox.Text = AppDataPaths.EdgeExtensionPath();
    Loaded += (_, _) => ExtensionIdBox.Focus();
  }

  public BridgeSetupResult? Result { get; private set; }

  private void Start_Click(object sender, RoutedEventArgs e)
  {
    if (!BridgeSetupResult.TryCreate(
            ExtensionIdBox.Text,
            SiteFamilyBox.Text,
            out var result))
    {
      ErrorText.Text = "请输入 Edge 显示的 32 位扩展 ID，以及有效的站点家族标识。";
      return;
    }

    Result = result;
    DialogResult = true;
  }

  private void CopyPath_Click(object sender, RoutedEventArgs e) =>
      Clipboard.SetText(ExtensionPathBox.Text);

  private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
