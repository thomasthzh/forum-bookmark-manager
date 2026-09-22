namespace Rfbm.Core.Models;

public enum PostStatus
{
  NotDownloaded,
  Downloaded,
  LinkInvalid
}

public static class PostStatusExtensions
{
  public static PostStatus Next(this PostStatus status)
  {
    return status switch
    {
      PostStatus.NotDownloaded => PostStatus.Downloaded,
      PostStatus.Downloaded => PostStatus.LinkInvalid,
      PostStatus.LinkInvalid => PostStatus.NotDownloaded,
      _ => PostStatus.NotDownloaded
    };
  }

  public static string DisplayName(this PostStatus status)
  {
    return status switch
    {
      PostStatus.NotDownloaded => "未下载",
      PostStatus.Downloaded => "已下载",
      PostStatus.LinkInvalid => "链接失效",
      _ => "未下载"
    };
  }

  public static string ToStorageValue(this PostStatus status)
  {
    return status switch
    {
      PostStatus.NotDownloaded => "not_downloaded",
      PostStatus.Downloaded => "downloaded",
      PostStatus.LinkInvalid => "link_invalid",
      _ => "not_downloaded"
    };
  }

  public static PostStatus FromStorageValue(string? value)
  {
    return value switch
    {
      "downloaded" => PostStatus.Downloaded,
      "link_invalid" => PostStatus.LinkInvalid,
      _ => PostStatus.NotDownloaded
    };
  }
}
