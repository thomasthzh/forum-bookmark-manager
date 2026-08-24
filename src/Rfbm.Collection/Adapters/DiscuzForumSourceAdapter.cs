using System.Collections.Immutable;
using Rfbm.Collection.Acquisition;
using Rfbm.Collection.Contracts;
using Rfbm.Core.Parsing;
using CoreFavoriteReference = Rfbm.Core.Models.FavoriteReference;

namespace Rfbm.Collection.Adapters;

public sealed class DiscuzForumSourceAdapter : IForumSourceAdapter, IFavoriteContextForumSourceAdapter
{
  public FavoritePageProjection ParseFavoritePage(PageSnapshot snapshot, SiteDefinition site)
  {
    var parsed = DiscuzPageParser.ParseFavorites(
        snapshot.Document,
        snapshot.FinalUri,
        site.Family.Value);
    var nextUri = parsed.Pagination.NextUrl is null
        ? null
        : new Uri(parsed.Pagination.NextUrl);

    return new FavoritePageProjection(
        parsed.Pagination.CurrentPage,
        parsed.Pagination.MaxPage,
        parsed.Pagination.PageUrls
            .Select(value => new Uri(value))
            .ToImmutableArray(),
        parsed.Items.Select(item => new FavoriteReference(
                new ThreadKey(new SiteFamily(item.Key.SiteFamily), item.Key.ThreadId),
                new Uri(item.Url),
                item.Title,
                item.RawFavoriteTime,
                item.FavoriteTime,
                new Uri(item.SourcePageUrl)))
            .ToImmutableArray(),
        nextUri,
        nextUri is null && parsed.Pagination.CurrentPage >= parsed.Pagination.MaxPage);
  }

  public ThreadProjection ParseThreadPage(PageSnapshot snapshot, ThreadKey expectedThread)
  {
    return ParseThreadPageCore(snapshot, expectedThread, null);
  }

  public ThreadProjection ParseThreadPage(PageSnapshot snapshot, FavoriteReference favorite)
  {
    CoreFavoriteReference? coreFavorite = null;
    if (favorite.SourcePageUri is not null)
    {
      coreFavorite = new CoreFavoriteReference(
          new Rfbm.Core.Models.ThreadKey(
              favorite.Key.SiteFamily.Value,
              favorite.Key.ThreadId),
          favorite.Uri.AbsoluteUri,
          favorite.Title ?? string.Empty,
          favorite.RawFavoriteTime,
          favorite.FavoriteTime,
          favorite.SourcePageUri.AbsoluteUri);
    }

    return ParseThreadPageCore(snapshot, favorite.Key, coreFavorite);
  }

  private static ThreadProjection ParseThreadPageCore(
      PageSnapshot snapshot,
      ThreadKey expectedThread,
      CoreFavoriteReference? favorite)
  {
    var logicalPostUri = favorite is null
        ? snapshot.RequestedUri
        : new Uri(favorite.Url);
    var parsed = DiscuzPageParser.ParseThread(
        snapshot.Document,
        logicalPostUri,
        expectedThread.SiteFamily.Value,
        favorite,
        contentBaseUrl: snapshot.FinalUri);
    var parsedKey = new ThreadKey(
        new SiteFamily(parsed.Key.SiteFamily),
        parsed.Key.ThreadId);
    if (parsedKey != expectedThread)
    {
      throw new InvalidDataException("Discuz page identity does not match the scheduled thread.");
    }

    return new ThreadProjection(parsedKey, parsed.Title, parsed);
  }
}
