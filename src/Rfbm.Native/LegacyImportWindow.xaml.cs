using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace Rfbm.Native;

public sealed record LegacyImportSetup(string SourcePath, string? SiteFamily);

public partial class LegacyImportWindow : Window
{
  public LegacyImportWindow()
  {
    InitializeComponent();
  }

  public LegacyImportSetup? Result { get; private set; }

  private void Browse_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog
    {
      Title = "选择旧版 R-fbm SQLite 数据库",
      Filter = "SQLite 数据库 (*.sqlite;*.sqlite3;*.db)|*.sqlite;*.sqlite3;*.db|所有文件 (*.*)|*.*",
      CheckFileExists = true,
      Multiselect = false
    };
    if (dialog.ShowDialog(this) == true)
    {
      SourcePathBox.Text = dialog.FileName;
    }
  }

  private void Import_Click(object sender, RoutedEventArgs e)
  {
    var sourcePath = SourcePathBox.Text.Trim();
    var siteFamily = SiteFamilyBox.Text.Trim().ToLowerInvariant();
    if (!File.Exists(sourcePath))
    {
      ErrorText.Text = "请选择存在的旧版 SQLite 文件。";
      return;
    }
    if (siteFamily.Length > 0 &&
        !Regex.IsMatch(siteFamily, "^[a-z0-9][a-z0-9._-]{0,63}$"))
    {
      ErrorText.Text = "站点家族标识只能包含字母、数字、点、下划线和连字符。";
      return;
    }

    Result = new LegacyImportSetup(sourcePath, siteFamily.Length == 0 ? null : siteFamily);
    DialogResult = true;
  }

  private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
