using Rfbm.Core.Models;
using Rfbm.Data.Storage;
using CoreObservationKind = Rfbm.Core.Models.ObservationKind;

namespace Rfbm.Collection.Adapters;

public static class CorePostCaptureMapper
{
  public static PostCapture Map(BookmarkPost post)
  {
    var password = post.ExtractPassword.Kind == CoreObservationKind.Unobservable
        ? post.AccessCode
        : post.ExtractPassword;
    return new PostCapture
    {
      Key = new PostKey(post.Key.SiteFamily, post.Key.ThreadId),
      CanonicalUrl = post.CanonicalUrl,
      RawUrl = OptionalString(post.Url),
      MirrorUrl = string.Equals(post.ContentUrl, post.CanonicalUrl, StringComparison.OrdinalIgnoreCase)
            ? Observation<string?>.Unobserved
            : OptionalString(post.ContentUrl),
      FavoriteUrl = OptionalString(post.FavoriteSourceUrl),
      Title = Observation<string>.Observed(post.Title),
      ProjectType = Observation<string>.Observed(post.Type),
      FavoriteTimeRaw = OptionalString(post.RawFavoriteTime),
      FavoriteTimeUtc = post.FavoriteTime is null
            ? Observation<DateTimeOffset?>.Unobserved
            : Observation<DateTimeOffset?>.Observed(post.FavoriteTime),
      DownloadCount = MapNullable(post.DownloadCount),
      VisitCount = MapNullable(post.VisitCount),
      FavoriteCount = MapNullable(post.FavoriteCount),
      ExtractPassword = MapNullable(password),
      BodyText = MapNullable(post.BodyText),
      Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(
            post.Images
                .Take(9)
                .Select(image => new CapturedPostImage(
                    image.Position,
                    image.SourceUrl,
                    image.LocalPath))
                .ToArray()),
      ResourceLinks = Observation<IReadOnlyList<CapturedResourceLink>>.Observed(
            post.ResourceLinks
                .Select((link, index) => new CapturedResourceLink(
                    index + 1,
                    link.Url,
                    link.Label,
                    link.ContextText))
                .ToArray())
    };
  }

  private static Observation<string?> OptionalString(string? value) =>
      string.IsNullOrWhiteSpace(value)
          ? Observation<string?>.Unobserved
          : Observation<string?>.Observed(value);

  private static Observation<int?> MapNullable(Observed<int> observed) => observed.Kind switch
  {
    CoreObservationKind.ObservedValue => Observation<int?>.Observed(observed.Value),
    CoreObservationKind.ObservedEmpty => Observation<int?>.Observed(null),
    _ => Observation<int?>.Unobserved
  };

  private static Observation<string?> MapNullable(Observed<string> observed) => observed.Kind switch
  {
    CoreObservationKind.ObservedValue => Observation<string?>.Observed(observed.Value),
    CoreObservationKind.ObservedEmpty => Observation<string?>.Observed(null),
    _ => Observation<string?>.Unobserved
  };
}
