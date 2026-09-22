using System.IO;

namespace Rfbm.Native.Services;

public static class AppDataPaths
{
  public static string RootDirectory()
  {
    var overrideDirectory = Environment.GetEnvironmentVariable("RFBM_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(overrideDirectory))
    {
      return Path.GetFullPath(overrideDirectory);
    }

    var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(baseDirectory, "R-fbm");
  }

  public static string DatabasePath()
  {
    return Path.Combine(RootDirectory(), "rfbm.sqlite3");
  }

  public static string EdgeProfilePath() => Path.Combine(RootDirectory(), "edge-profile");

  public static string ImagesPath() => Path.Combine(RootDirectory(), "images");

  public static string LogsPath() => Path.Combine(RootDirectory(), "logs");

  public static string SelectorProfilesPath() => Path.Combine(RootDirectory(), "selector-profiles");

  public static string EdgeExtensionPath() => Path.Combine(AppContext.BaseDirectory, "edge-extension");
}
