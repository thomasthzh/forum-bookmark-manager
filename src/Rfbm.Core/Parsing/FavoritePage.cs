using Rfbm.Core.Models;

namespace Rfbm.Core.Parsing;

public sealed record PaginationEvidence(
    int CurrentPage,
    int MaxPage,
    IReadOnlyList<string> PageUrls,
    string? NextUrl)
{
  public bool HasMorePages => CurrentPage < MaxPage || NextUrl is not null;
}

public sealed record FavoritePage(
    IReadOnlyList<FavoriteReference> Items,
    PaginationEvidence Pagination);
