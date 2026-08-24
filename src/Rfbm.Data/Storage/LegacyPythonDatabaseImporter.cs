using System.Globalization;
using Microsoft.Data.Sqlite;
using Rfbm.Core.Models;

namespace Rfbm.Data.Storage;

public sealed class LegacyPythonDatabaseImporter
{
  private readonly BookmarkDatabase _destination;

  public LegacyPythonDatabaseImporter(BookmarkDatabase destination)
  {
    _destination = destination ?? throw new ArgumentNullException(nameof(destination));
  }

  public async Task<LegacyImportResult> ImportAsync(
      string sourcePath,
      Func<string, PostKey> identityResolver,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
    ArgumentNullException.ThrowIfNull(identityResolver);
    var fullSourcePath = Path.GetFullPath(sourcePath);
    if (!File.Exists(fullSourcePath))
    {
      throw new FileNotFoundException("Legacy Python database was not found.", fullSourcePath);
    }
    if (string.Equals(fullSourcePath, _destination.DatabasePath, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException("Import requires separate source and destination database files.");
    }

    await _destination.InitializeAsync(cancellationToken);
    await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
    {
      DataSource = fullSourcePath,
      Mode = SqliteOpenMode.ReadOnly,
      Cache = SqliteCacheMode.Private,
      Pooling = false
    }.ToString());
    await source.OpenAsync(cancellationToken);
    await VerifyLegacySchemaAsync(source, cancellationToken);
    var rows = await ReadLegacyPostsAsync(source, cancellationToken);
    var repository = new PostRepository(_destination);

    foreach (var row in rows)
    {
      var images = await ReadImagesAsync(source, row.Id, cancellationToken);
      var links = await ReadLinksAsync(source, row.Id, cancellationToken);
      var favoriteTimeUtc = TryParseTimestamp(row.FavoriteTime);
      var key = identityResolver(row.PostUrl);

      await repository.UpsertCaptureAsync(new PostCapture
      {
        Key = key,
        CanonicalUrl = row.PostUrl,
        RawUrl = Observation<string?>.Observed(row.PostUrl),
        FavoriteUrl = Observation<string?>.Observed(row.FavoriteUrl),
        Title = Observation<string>.Observed(row.Title),
        ProjectType = Observation<string>.Observed(row.ProjectType),
        FavoriteTimeRaw = Observation<string?>.Observed(row.FavoriteTime),
        FavoriteTimeUtc = Observation<DateTimeOffset?>.Observed(favoriteTimeUtc),
        DownloadCount = Observation<int?>.Observed(row.DownloadCount),
        VisitCount = Observation<int?>.Observed(row.VisitCount),
        FavoriteCount = Observation<int?>.Observed(row.FavoriteCount),
        ExtractPassword = Observation<string?>.Observed(row.ExtractPassword),
        BodyText = Observation<string?>.Observed(row.BodyText),
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(images),
        ResourceLinks = Observation<IReadOnlyList<CapturedResourceLink>>.Observed(links)
      }, cancellationToken);
      await repository.SetStatusAsync(key, ParseStatus(row.Status), cancellationToken);
    }

    return new LegacyImportResult(rows.Count);
  }

  private static async Task VerifyLegacySchemaAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(posts);";
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      columns.Add(reader.GetString(1));
    }

    if (!columns.Contains("id") || !columns.Contains("post_url") || !columns.Contains("project_type"))
    {
      throw new InvalidOperationException("The selected file is not a supported Python bookmark database.");
    }
  }

  private static async Task<IReadOnlyList<LegacyPostRow>> ReadLegacyPostsAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT id, post_url, favorite_url, title, project_type, status, favorite_time,
                   download_count, visit_count, favorite_count, extract_password, body_text
            FROM posts ORDER BY id;
            """;
    var rows = new List<LegacyPostRow>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      rows.Add(new LegacyPostRow(
          reader.GetInt64(0),
          reader.GetString(1),
          NullableString(reader, 2),
          reader.GetString(3),
          reader.GetString(4),
          reader.GetString(5),
          NullableString(reader, 6),
          NullableInt(reader, 7),
          NullableInt(reader, 8),
          NullableInt(reader, 9),
          NullableString(reader, 10),
          NullableString(reader, 11)));
    }

    return rows;
  }

  private static async Task<IReadOnlyList<CapturedPostImage>> ReadImagesAsync(
      SqliteConnection connection,
      long postId,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT position, source_url, local_path, thumbnail_path, download_status
            FROM post_images WHERE post_id = $post_id ORDER BY position;
            """;
    command.Parameters.AddWithValue("$post_id", postId);
    var images = new List<CapturedPostImage>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      images.Add(new CapturedPostImage(
          reader.GetInt32(0),
          reader.GetString(1),
          NullableString(reader, 2),
          NullableString(reader, 3),
          reader.GetString(4)));
    }
    return images;
  }

  private static async Task<IReadOnlyList<CapturedResourceLink>> ReadLinksAsync(
      SqliteConnection connection,
      long postId,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT position, url, label, context_text
            FROM download_links WHERE post_id = $post_id ORDER BY position;
            """;
    command.Parameters.AddWithValue("$post_id", postId);
    var links = new List<CapturedResourceLink>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      links.Add(new CapturedResourceLink(
          reader.GetInt32(0),
          reader.GetString(1),
          NullableString(reader, 2) ?? string.Empty,
          NullableString(reader, 3) ?? string.Empty));
    }
    return links;
  }

  private static DateTimeOffset? TryParseTimestamp(string? value)
  {
    return DateTimeOffset.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out var parsed)
        ? parsed
        : null;
  }

  private static PostStatus ParseStatus(string value) => value switch
  {
    "downloaded" or "已下载" => PostStatus.Downloaded,
    "link_invalid" or "链接失效" => PostStatus.LinkInvalid,
    _ => PostStatus.NotDownloaded
  };

  private static string? NullableString(SqliteDataReader reader, int ordinal) =>
      reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

  private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
      reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

  private sealed record LegacyPostRow(
      long Id,
      string PostUrl,
      string? FavoriteUrl,
      string Title,
      string ProjectType,
      string Status,
      string? FavoriteTime,
      int? DownloadCount,
      int? VisitCount,
      int? FavoriteCount,
      string? ExtractPassword,
      string? BodyText);
}

public sealed record LegacyImportResult(int ImportedCount);
