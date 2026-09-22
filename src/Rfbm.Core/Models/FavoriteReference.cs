namespace Rfbm.Core.Models;

public sealed record FavoriteReference(
    ThreadKey Key,
    string Url,
    string Title,
    string? RawFavoriteTime,
    DateTimeOffset? FavoriteTime,
    string SourcePageUrl);
