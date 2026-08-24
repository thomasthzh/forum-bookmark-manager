using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Rfbm.Core.Models;

namespace Rfbm.Data.Storage;

public sealed class PostRepository
{
  private readonly BookmarkDatabase _database;

  public PostRepository(BookmarkDatabase database)
  {
    _database = database;
  }

  public async Task UpsertAsync(BookmarkPost post, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(post.Id);
    ArgumentException.ThrowIfNullOrWhiteSpace(post.Url);
    ArgumentException.ThrowIfNullOrWhiteSpace(post.Title);

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);

    var now = DateTimeOffset.UtcNow.ToString("O");

    await using var command = connection.CreateCommand();
    command.CommandText = """
            INSERT INTO posts (
                site_family, thread_id, canonical_url, raw_url, title, project_type, status,
                favorite_time_raw, favorite_time_utc, created_at, updated_at, last_crawled_at)
            VALUES (
                'legacy', $id, $url, $url, $title, $type, $status,
                $favorite_time, $favorite_time, $created_at, $updated_at, $updated_at)
            ON CONFLICT(site_family, thread_id) DO UPDATE SET
                canonical_url = excluded.canonical_url,
                raw_url = excluded.raw_url,
                title = excluded.title,
                project_type = excluded.project_type,
                favorite_time_raw = excluded.favorite_time_raw,
                favorite_time_utc = excluded.favorite_time_utc,
                updated_at = excluded.updated_at,
                last_crawled_at = excluded.last_crawled_at;
            """;
    AddPostParameters(command, post, post.Status, now);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  public async Task<BookmarkPost?> GetAsync(string id, CancellationToken cancellationToken = default)
  {
    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    return await GetAsync(connection, id, cancellationToken);
  }

  public async Task<IReadOnlyList<BookmarkPost>> ListAsync(CancellationToken cancellationToken = default)
  {
    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);

    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT thread_id, canonical_url, title, project_type, status, favorite_time_utc
            FROM posts
            WHERE deleted_at IS NULL
            ORDER BY
                CASE WHEN favorite_time_utc IS NULL THEN 1 ELSE 0 END,
                favorite_time_utc DESC,
                updated_at DESC,
                row_id DESC;
            """;

    var posts = new List<BookmarkPost>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      posts.Add(ReadPost(reader));
    }

    return posts;
  }

  public async Task SetStatusAsync(string id, PostStatus status, CancellationToken cancellationToken = default)
  {
    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);

    await using var command = connection.CreateCommand();
    command.CommandText = """
            UPDATE posts
            SET status = $status,
                updated_at = $updated_at
            WHERE site_family = 'legacy' AND thread_id = $id;
            """;
    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$status", status.ToStorageValue());
    command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  public Task<StoredPost> UpsertCaptureAsync(
      PostCapture capture,
      CancellationToken cancellationToken = default) =>
      UpsertCaptureCoreAsync(capture, null, cancellationToken);

  public Task<StoredPost> CommitCaptureAsync(
      PostCapture capture,
      string runId,
      string workItemKey,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(runId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workItemKey);
    var projection = Encoding.UTF8.GetBytes(
        $"{capture.Key}\n{capture.CanonicalUrl}\n{capture.Title.Value}\n{capture.BodyText.Value}");
    var pendingMedia = capture.Images.IsObserved &&
        (capture.Images.Value?.Count ?? 0) > 0;
    return CommitCaptureAsync(
        capture,
        new CaptureSnapshotMetadata
        {
          RunId = runId,
          WorkItemKey = workItemKey,
          PostKey = capture.Key,
          RequestedUrl = capture.CanonicalUrl,
          FinalUrl = capture.CanonicalUrl,
          Kind = "post-projection",
          Sha256 = Convert.ToHexString(SHA256.HashData(projection)).ToLowerInvariant(),
          CapturedAt = DateTimeOffset.UtcNow,
          ByteCount = projection.LongLength,
          MediaPending = pendingMedia,
          WorkParametersJson = pendingMedia
                ? $"{{\"remaining\":{capture.Images.Value!.Count}}}"
                : null
        },
        cancellationToken);
  }

  public Task<StoredPost> CommitCaptureAsync(
      PostCapture capture,
      CaptureSnapshotMetadata snapshot,
      CancellationToken cancellationToken = default)
  {
    ValidateSnapshot(capture, snapshot);
    return UpsertCaptureCoreAsync(capture, snapshot, cancellationToken);
  }

  private async Task<StoredPost> UpsertCaptureCoreAsync(
      PostCapture capture,
      CaptureSnapshotMetadata? snapshot,
      CancellationToken cancellationToken)
  {
    ValidateCapture(capture);
    var observedImages = capture.Images.IsObserved
        ? capture.Images.Value ?? Array.Empty<CapturedPostImage>()
        : Array.Empty<CapturedPostImage>();
    if (capture.Images.IsObserved)
    {
      ValidateImages(observedImages);
    }

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = connection.BeginTransaction(deferred: false);

    var rowId = await FindRowIdAsync(connection, transaction, capture.Key, cancellationToken);
    var timestamp = DateTimeOffset.UtcNow.ToString("O");
    if (rowId is null)
    {
      rowId = await InsertCaptureAsync(connection, transaction, capture, timestamp, cancellationToken);
    }
    else
    {
      await UpdateCaptureAsync(connection, transaction, rowId.Value, capture, timestamp, cancellationToken);
    }

    var generationImages = observedImages;
    if (snapshot is not null && !capture.Images.IsObserved)
    {
      generationImages = await ReadCurrentGenerationImagesAsync(
          connection,
          transaction,
          rowId.Value,
          cancellationToken);
    }

    var captureGeneration = 0L;
    long? snapshotId = null;
    if (snapshot is not null)
    {
      await EnsureCaptureWorkExistsAsync(connection, transaction, snapshot, cancellationToken);
      var snapshotReference = await UpsertSnapshotAsync(
          connection,
          transaction,
          rowId.Value,
          snapshot,
          generationImages.Count,
          cancellationToken);
      snapshotId = snapshotReference.SnapshotId;
      captureGeneration = snapshotReference.CaptureGeneration;
      await EnsureExpectedImagesAsync(
          connection,
          transaction,
          snapshotReference.SnapshotId,
          generationImages,
          snapshotReference.IsNew,
          cancellationToken);
    }

    if (capture.Images.IsObserved || (snapshot is not null && generationImages.Count > 0))
    {
      var stagedImages = capture.Images.IsObserved && snapshot is not null && !snapshot.MediaPending
          ? generationImages
              .Select(image => image.DownloadStatus == "pending"
                  ? image with { DownloadStatus = "skipped" }
                  : image)
              .ToArray()
          : generationImages;
      await ReplaceImagesAsync(
          connection,
          transaction,
          rowId.Value,
          captureGeneration,
          stagedImages,
          cancellationToken);
    }

    if (capture.ResourceLinks.IsObserved)
    {
      await ReplaceResourceLinksAsync(
          connection,
          transaction,
          rowId.Value,
          capture.ResourceLinks.Value ?? Array.Empty<CapturedResourceLink>(),
          cancellationToken);
    }

    if (snapshot is not null)
    {
      await using var checkpoint = connection.CreateCommand();
      checkpoint.Transaction = transaction;
      checkpoint.CommandText = """
                UPDATE collection_work_items
                SET state = 'captured',
                     post_id = $post_id,
                     snapshot_id = $snapshot_id,
                     capture_generation = $capture_generation,
                     media_pending = $media_pending,
                    work_parameters_json = $work_parameters_json,
                    captured_at = $captured_at,
                    committed_at = NULL
                WHERE run_id = $run_id
                  AND logical_key = $logical_key
                  AND state <> 'committed';
                """;
      checkpoint.Parameters.AddWithValue("$post_id", rowId.Value);
      checkpoint.Parameters.AddWithValue("$snapshot_id", snapshotId!.Value);
      checkpoint.Parameters.AddWithValue("$capture_generation", captureGeneration);
      checkpoint.Parameters.AddWithValue("$media_pending", snapshot.MediaPending ? 1 : 0);
      checkpoint.Parameters.AddWithValue("$work_parameters_json", DbValue(snapshot.WorkParametersJson));
      checkpoint.Parameters.AddWithValue("$captured_at", snapshot.CapturedAt.ToUniversalTime().ToString("O"));
      checkpoint.Parameters.AddWithValue("$run_id", snapshot.RunId);
      checkpoint.Parameters.AddWithValue("$logical_key", snapshot.WorkItemKey);
      if (await checkpoint.ExecuteNonQueryAsync(cancellationToken) != 1)
      {
        throw new InvalidOperationException(
            "The collection work item is missing or already committed.");
      }
    }

    await transaction.CommitAsync(cancellationToken);
    return await GetAsync(capture.Key, includeDeleted: true, cancellationToken)
        ?? throw new InvalidOperationException("The captured post was not persisted.");
  }

  public async Task<StoredCaptureSnapshot?> GetCaptureSnapshotAsync(
      string runId,
      string workItemKey,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(runId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workItemKey);
    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT s.snapshot_id, s.run_id, s.work_item_key, s.post_id,
                   p.site_family, p.thread_id,
                   s.requested_url, s.final_url, s.kind, s.sha256, s.captured_at, s.byte_count,
                   s.capture_generation, s.expected_image_count,
                   w.state, w.media_pending, w.work_parameters_json
            FROM capture_snapshots s
            JOIN posts p ON p.row_id = s.post_id
            JOIN collection_work_items w
              ON w.run_id = s.run_id
             AND w.logical_key = s.work_item_key
             AND w.snapshot_id = s.snapshot_id
             AND w.post_id = s.post_id
             AND w.capture_generation = s.capture_generation
            WHERE s.run_id = $run_id AND s.work_item_key = $work_item_key
            ORDER BY s.captured_at DESC, s.snapshot_id DESC
            LIMIT 1;
            """;
    command.Parameters.AddWithValue("$run_id", runId);
    command.Parameters.AddWithValue("$work_item_key", workItemKey);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
      return null;
    }

    return new StoredCaptureSnapshot(
        SnapshotId: reader.GetInt64(0),
        RunId: reader.GetString(1),
        WorkItemKey: reader.GetString(2),
        PostRowId: reader.GetInt64(3),
        PostKey: new PostKey(reader.GetString(4), reader.GetString(5)),
        RequestedUrl: reader.GetString(6),
        FinalUrl: reader.GetString(7),
        Kind: reader.GetString(8),
        Sha256: reader.GetString(9),
        CapturedAt: DateTimeOffset.Parse(reader.GetString(10)),
        ByteCount: reader.GetInt64(11),
        CaptureGeneration: reader.GetInt64(12),
        ExpectedImageCount: reader.GetInt32(13),
        WorkState: reader.GetString(14),
        MediaPending: reader.GetInt32(15) != 0,
        WorkParametersJson: reader.IsDBNull(16) ? null : reader.GetString(16));
  }

  public async Task<bool> FinalizeCaptureAsync(
      string runId,
      string workItemKey,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(runId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workItemKey);
    var snapshot = await GetCaptureSnapshotAsync(runId, workItemKey, cancellationToken)
        ?? throw new InvalidOperationException(
            "The collection work item has no current capture snapshot to finalize.");
    return await FinalizeCaptureAsync(
        new CaptureFinalizeRequest
        {
          RunId = runId,
          WorkItemKey = workItemKey,
          PostKey = snapshot.PostKey,
          SnapshotId = snapshot.SnapshotId,
          CaptureGeneration = snapshot.CaptureGeneration
        },
        cancellationToken);
  }

  public async Task<bool> FinalizeCaptureAsync(
      CaptureFinalizeRequest request,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkItemKey);
    ValidateKey(request.PostKey);
    if (request.SnapshotId <= 0)
    {
      throw new ArgumentOutOfRangeException(
          nameof(request), "A positive capture snapshot identifier is required.");
    }
    if (request.CaptureGeneration < 0)
    {
      throw new ArgumentOutOfRangeException(
          nameof(request), "Capture generation cannot be negative.");
    }

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = connection.BeginTransaction(deferred: false);

    string workState;
    string runState;
    long postId;
    int expectedImageCount;
    await using (var binding = connection.CreateCommand())
    {
      binding.Transaction = transaction;
      binding.CommandText = """
              SELECT w.state, r.state, w.post_id, w.snapshot_id, w.capture_generation,
                     w.media_pending, p.site_family, p.thread_id,
                     s.run_id, s.work_item_key, s.post_id, s.capture_generation,
                     s.expected_image_count
              FROM collection_work_items w
              JOIN collection_runs r ON r.id = w.run_id
              LEFT JOIN posts p ON p.row_id = w.post_id
              LEFT JOIN capture_snapshots s ON s.snapshot_id = w.snapshot_id
              WHERE w.run_id = $run_id AND w.logical_key = $logical_key;
              """;
      binding.Parameters.AddWithValue("$run_id", request.RunId);
      binding.Parameters.AddWithValue("$logical_key", request.WorkItemKey);
      await using var reader = await binding.ExecuteReaderAsync(cancellationToken);
      if (!await reader.ReadAsync(cancellationToken))
      {
        throw new InvalidOperationException("The collection work item does not exist.");
      }

      workState = reader.GetString(0);
      runState = reader.GetString(1);
      if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(6) ||
          reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9) ||
          reader.IsDBNull(10) || reader.IsDBNull(11))
      {
        throw new InvalidOperationException(
            "The work item, post, and capture snapshot binding is incomplete.");
      }

      postId = reader.GetInt64(2);
      var workSnapshotId = reader.GetInt64(3);
      var workGeneration = reader.GetInt64(4);
      var storedPostKey = new PostKey(reader.GetString(6), reader.GetString(7));
      var snapshotRunId = reader.GetString(8);
      var snapshotWorkItemKey = reader.GetString(9);
      var snapshotPostId = reader.GetInt64(10);
      var snapshotGeneration = reader.GetInt64(11);
      expectedImageCount = reader.GetInt32(12);
      if (workSnapshotId != request.SnapshotId ||
          workGeneration != request.CaptureGeneration ||
          storedPostKey != request.PostKey ||
          snapshotRunId != request.RunId ||
          snapshotWorkItemKey != request.WorkItemKey ||
          snapshotPostId != postId ||
          snapshotGeneration != workGeneration)
      {
        throw new InvalidOperationException(
            "The finalize request does not identify the work item's current post and capture generation.");
      }
    }

    long manifestCount;
    long generationImageCount;
    long terminalImageCount;
    await using (var images = connection.CreateCommand())
    {
      images.Transaction = transaction;
      images.CommandText = """
              SELECT
                (SELECT count(*)
                 FROM capture_expected_images e
                 WHERE e.snapshot_id = $snapshot_id),
                (SELECT count(*)
                 FROM post_images i
                 WHERE i.post_id = $post_id
                   AND i.capture_generation = $capture_generation),
                (SELECT count(*)
                 FROM capture_expected_images e
                 JOIN post_images i
                   ON i.post_id = $post_id
                  AND i.capture_generation = $capture_generation
                  AND i.position = e.position
                  AND i.source_url = e.source_url
                 WHERE e.snapshot_id = $snapshot_id
                   AND (
                     (i.download_status = 'downloaded'
                         AND NULLIF(TRIM(i.local_path), '') IS NOT NULL)
                     OR i.download_status IN ('failed', 'skipped')));
              """;
      images.Parameters.AddWithValue("$snapshot_id", request.SnapshotId);
      images.Parameters.AddWithValue("$post_id", postId);
      images.Parameters.AddWithValue("$capture_generation", request.CaptureGeneration);
      await using var reader = await images.ExecuteReaderAsync(cancellationToken);
      await reader.ReadAsync(cancellationToken);
      manifestCount = reader.GetInt64(0);
      generationImageCount = reader.GetInt64(1);
      terminalImageCount = reader.GetInt64(2);
    }

    if (manifestCount != expectedImageCount ||
        generationImageCount != expectedImageCount ||
        terminalImageCount != expectedImageCount)
    {
      throw new InvalidOperationException(
          "The current capture generation is missing expected images, contains replacements, or has unfinished media.");
    }

    if (workState == "committed")
    {
      await transaction.CommitAsync(cancellationToken);
      return false;
    }
    if (workState != "captured" || !IsActiveRunState(runState))
    {
      throw new InvalidOperationException(
          "Only a captured work item in an active run can be finalized.");
    }

    var timestamp = DateTimeOffset.UtcNow.ToString("O");
    await using (var work = connection.CreateCommand())
    {
      work.Transaction = transaction;
      work.CommandText = """
              UPDATE collection_work_items
              SET state = 'committed', media_pending = 0, committed_at = $committed_at
              WHERE run_id = $run_id
                AND logical_key = $logical_key
                AND state = 'captured'
                AND post_id = $post_id
                AND snapshot_id = $snapshot_id
                AND capture_generation = $capture_generation;
              """;
      work.Parameters.AddWithValue("$committed_at", timestamp);
      work.Parameters.AddWithValue("$run_id", request.RunId);
      work.Parameters.AddWithValue("$logical_key", request.WorkItemKey);
      work.Parameters.AddWithValue("$post_id", postId);
      work.Parameters.AddWithValue("$snapshot_id", request.SnapshotId);
      work.Parameters.AddWithValue("$capture_generation", request.CaptureGeneration);
      if (await work.ExecuteNonQueryAsync(cancellationToken) != 1)
      {
        throw new InvalidOperationException("The capture binding changed before it could be finalized.");
      }
    }

    await using (var run = connection.CreateCommand())
    {
      run.Transaction = transaction;
      run.CommandText = """
              UPDATE collection_runs
              SET threads_committed = threads_committed + 1
              WHERE id = $run_id
                AND state IN (
                  'Preparing', 'WaitingForUser', 'Discovering',
                  'Collecting', 'CancelRequested');
              """;
      run.Parameters.AddWithValue("$run_id", request.RunId);
      if (await run.ExecuteNonQueryAsync(cancellationToken) != 1)
      {
        throw new InvalidOperationException("The collection run is not active.");
      }
    }

    await transaction.CommitAsync(cancellationToken);
    return true;
  }

  public async Task<StoredPost?> GetAsync(
      PostKey key,
      bool includeDeleted = false,
      CancellationToken cancellationToken = default)
  {
    ValidateKey(key);
    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);

    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT row_id, site_family, thread_id, canonical_url, raw_url, mirror_url, favorite_url,
                   title, project_type, status, favorite_time_raw, favorite_time_utc,
                   download_count, visit_count, favorite_count, extract_password, body_text,
                   is_starred, note, deleted_at, created_at, updated_at, last_crawled_at
            FROM posts
            WHERE site_family = $site_family
              AND thread_id = $thread_id
              AND ($include_deleted = 1 OR deleted_at IS NULL);
            """;
    command.Parameters.AddWithValue("$site_family", key.SiteFamily);
    command.Parameters.AddWithValue("$thread_id", key.ThreadId);
    command.Parameters.AddWithValue("$include_deleted", includeDeleted ? 1 : 0);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
      return null;
    }

    var stored = ReadStoredPost(reader);
    await reader.DisposeAsync();
    var images = await ReadImagesAsync(connection, stored.RowId, cancellationToken);
    var links = await ReadResourceLinksAsync(connection, stored.RowId, cancellationToken);
    var tags = await ReadTagsAsync(connection, stored.RowId, cancellationToken);
    return stored with { Images = images, ResourceLinks = links, Tags = tags };
  }

  public Task<bool> SetStatusAsync(
      PostKey key,
      PostStatus status,
      CancellationToken cancellationToken = default) =>
      UpdateManualColumnAsync(key, "status", status.ToStorageValue(), cancellationToken);

  public Task<bool> SetStarredAsync(
      PostKey key,
      bool isStarred,
      CancellationToken cancellationToken = default) =>
      UpdateManualColumnAsync(key, "is_starred", isStarred ? 1 : 0, cancellationToken);

  public Task<bool> SetNoteAsync(
    PostKey key,
    string? note,
    CancellationToken cancellationToken = default) =>
    UpdateManualColumnAsync(key, "note", note?.Trim() ?? string.Empty, cancellationToken);

  public Task<bool> UpdateImageDownloadAsync(
      PostKey key,
      string sourceUrl,
      string? localPath,
      string? thumbnailPath,
      string downloadStatus,
      CancellationToken cancellationToken = default) =>
      UpdateImageDownloadCoreAsync(
          key,
          snapshotId: null,
          captureGeneration: 0,
          sourceUrl,
          localPath,
          thumbnailPath,
          downloadStatus,
          cancellationToken);

  public Task<bool> UpdateImageDownloadAsync(
      ImageDownloadUpdateRequest request,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.SnapshotId <= 0)
    {
      throw new ArgumentOutOfRangeException(
          nameof(request), "A positive capture snapshot identifier is required.");
    }
    if (request.CaptureGeneration < 0)
    {
      throw new ArgumentOutOfRangeException(
          nameof(request), "Capture generation cannot be negative.");
    }

    return UpdateImageDownloadCoreAsync(
        request.PostKey,
        request.SnapshotId,
        request.CaptureGeneration,
        request.SourceUrl,
        request.LocalPath,
        request.ThumbnailPath,
        request.DownloadStatus,
        cancellationToken);
  }

  private async Task<bool> UpdateImageDownloadCoreAsync(
      PostKey key,
      long? snapshotId,
      long captureGeneration,
      string sourceUrl,
      string? localPath,
      string? thumbnailPath,
      string downloadStatus,
      CancellationToken cancellationToken)
  {
    ValidateKey(key);
    ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
    if (downloadStatus is not ("downloaded" or "failed"))
    {
      throw new ArgumentException(
          "Image download updates must report either downloaded or failed.",
          nameof(downloadStatus));
    }

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = connection.BeginTransaction(deferred: false);
    long imageId;
    CapturedPostImage? existing;
    await using (var select = connection.CreateCommand())
    {
      select.Transaction = transaction;
      select.CommandText = snapshotId is null
          ? """
            SELECT i.image_id, i.position, i.source_url, i.local_path, i.thumbnail_path,
                   i.download_status, i.capture_generation
            FROM post_images i
            JOIN posts p ON p.row_id = i.post_id
            WHERE p.site_family = $site_family
              AND p.thread_id = $thread_id
              AND i.capture_generation = 0
              AND i.source_url = $source_url
            LIMIT 1;
            """
          : """
            SELECT i.image_id, i.position, i.source_url, i.local_path, i.thumbnail_path,
                   i.download_status, i.capture_generation
            FROM post_images i
            JOIN posts p ON p.row_id = i.post_id
            JOIN capture_snapshots s
              ON s.snapshot_id = $snapshot_id
             AND s.post_id = i.post_id
             AND s.capture_generation = i.capture_generation
            JOIN collection_work_items w
              ON w.run_id = s.run_id
             AND w.logical_key = s.work_item_key
             AND w.post_id = s.post_id
             AND w.snapshot_id = s.snapshot_id
             AND w.capture_generation = s.capture_generation
             AND w.state = 'captured'
            JOIN capture_expected_images e
              ON e.snapshot_id = s.snapshot_id
             AND e.position = i.position
             AND e.source_url = i.source_url
            WHERE p.site_family = $site_family
              AND p.thread_id = $thread_id
              AND i.capture_generation = $capture_generation
              AND i.source_url = $source_url
            LIMIT 1;
            """;
      select.Parameters.AddWithValue("$site_family", key.SiteFamily);
      select.Parameters.AddWithValue("$thread_id", key.ThreadId);
      select.Parameters.AddWithValue("$source_url", sourceUrl);
      if (snapshotId is not null)
      {
        select.Parameters.AddWithValue("$snapshot_id", snapshotId.Value);
        select.Parameters.AddWithValue("$capture_generation", captureGeneration);
      }
      await using var reader = await select.ExecuteReaderAsync(cancellationToken);
      if (await reader.ReadAsync(cancellationToken))
      {
        imageId = reader.GetInt64(0);
        existing = new CapturedPostImage(
            reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));
      }
      else
      {
        imageId = 0;
        existing = null;
      }
    }

    if (existing is null)
    {
      if (snapshotId is not null)
      {
        throw new InvalidOperationException(
            "The image update does not belong to the current captured snapshot generation.");
      }

      return false;
    }

    var existingSucceeded = IsSuccessfulImage(existing);
    var incomingSucceeded = downloadStatus == "downloaded" && !string.IsNullOrWhiteSpace(localPath);
    if (existingSucceeded && !incomingSucceeded)
    {
      await transaction.CommitAsync(cancellationToken);
      return true;
    }

    var nextLocalPath = NonBlankOrNull(localPath);
    var nextThumbnailPath = NonBlankOrNull(thumbnailPath) ??
        (incomingSucceeded ? existing.ThumbnailPath : null);
    await using (var update = connection.CreateCommand())
    {
      update.Transaction = transaction;
      update.CommandText = """
        UPDATE post_images
        SET local_path = $local_path,
            thumbnail_path = $thumbnail_path,
            download_status = $download_status
        WHERE image_id = $image_id;
        """;
      update.Parameters.AddWithValue("$local_path", DbValue(nextLocalPath));
      update.Parameters.AddWithValue("$thumbnail_path", DbValue(nextThumbnailPath));
      update.Parameters.AddWithValue("$download_status", downloadStatus);
      update.Parameters.AddWithValue("$image_id", imageId);
      if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
      {
        throw new InvalidOperationException("The staged image changed before its result was recorded.");
      }
    }

    await using var touch = connection.CreateCommand();
    touch.Transaction = transaction;
    touch.CommandText = """
      UPDATE posts SET updated_at = $updated_at
      WHERE site_family = $site_family AND thread_id = $thread_id;
      """;
    touch.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
    touch.Parameters.AddWithValue("$site_family", key.SiteFamily);
    touch.Parameters.AddWithValue("$thread_id", key.ThreadId);
    await touch.ExecuteNonQueryAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return true;
  }

  public async Task<bool> ReplaceTagsAsync(
        PostKey key,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(tags);
    ValidateKey(key);
    var normalized = tags
        .Select(tag => tag?.Trim())
        .Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Select(tag => tag!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = connection.BeginTransaction(deferred: false);
    var rowId = await FindRowIdAsync(connection, transaction, key, cancellationToken);
    if (rowId is null)
    {
      return false;
    }

    await using (var delete = connection.CreateCommand())
    {
      delete.Transaction = transaction;
      delete.CommandText = "DELETE FROM post_tags WHERE post_id = $post_id;";
      delete.Parameters.AddWithValue("$post_id", rowId.Value);
      await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    foreach (var tag in normalized)
    {
      await using var insert = connection.CreateCommand();
      insert.Transaction = transaction;
      insert.CommandText = """
                INSERT INTO tags (name) VALUES ($name)
                ON CONFLICT(name) DO NOTHING;
                INSERT INTO post_tags (post_id, tag_id)
                SELECT $post_id, tag_id FROM tags WHERE name = $name COLLATE NOCASE;
                """;
      insert.Parameters.AddWithValue("$name", tag);
      insert.Parameters.AddWithValue("$post_id", rowId.Value);
      await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    await transaction.CommitAsync(cancellationToken);
    return true;
  }

  public async Task<PostPage> QueryAsync(
      PostQuery query,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(query);
    if (query.Offset < 0 || query.Limit is < 1 or > 500)
    {
      throw new ArgumentOutOfRangeException(nameof(query), "Offset must be non-negative and limit must be between 1 and 500.");
    }

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    var (where, parameters) = BuildQueryWhere(query);

    int total;
    await using (var count = connection.CreateCommand())
    {
      count.CommandText = $"SELECT count(*) FROM posts p {where};";
      AddParameters(count, parameters);
      total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
    }

    var orderBy = query.Sort switch
    {
      PostSort.OldestFavorite =>
          "p.favorite_time_utc IS NULL, p.favorite_time_utc ASC, p.row_id ASC",
      PostSort.RecentlyUpdated => "p.updated_at DESC, p.row_id DESC",
      _ => "p.favorite_time_utc IS NULL, p.favorite_time_utc DESC, p.row_id DESC"
    };

    await using var command = connection.CreateCommand();
    command.CommandText = $"""
            SELECT p.row_id, p.site_family, p.thread_id, p.canonical_url, p.raw_url, p.mirror_url,
                   p.favorite_url, p.title, p.project_type, p.status, p.favorite_time_raw,
                   p.favorite_time_utc, p.download_count, p.visit_count, p.favorite_count,
                   p.extract_password, p.body_text, p.is_starred, p.note, p.deleted_at,
                   p.created_at, p.updated_at, p.last_crawled_at
            FROM posts p
            {where}
            ORDER BY {orderBy}
            LIMIT $limit OFFSET $offset;
            """;
    AddParameters(command, parameters);
    command.Parameters.AddWithValue("$limit", query.Limit);
    command.Parameters.AddWithValue("$offset", query.Offset);

    var posts = new List<StoredPost>();
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
    {
      while (await reader.ReadAsync(cancellationToken))
      {
        posts.Add(ReadStoredPost(reader));
      }
    }

    var hydrated = await HydratePostsAsync(connection, posts, cancellationToken);
    return new PostPage(hydrated, total, query.Offset, query.Limit);
  }

  public Task<int> SetStatusManyAsync(
      IEnumerable<PostKey> keys,
      PostStatus status,
      CancellationToken cancellationToken = default) =>
      UpdateManyAsync(
          keys,
          "status = $value, updated_at = $timestamp",
          string.Empty,
          status.ToStorageValue(),
          cancellationToken);

  public Task<int> MoveToTrashAsync(
      IEnumerable<PostKey> keys,
      CancellationToken cancellationToken = default) =>
      UpdateManyAsync(
          keys,
          "deleted_at = $timestamp, updated_at = $timestamp",
          "deleted_at IS NULL",
          value: null,
          cancellationToken);

  public Task<int> RestoreAsync(
      IEnumerable<PostKey> keys,
      CancellationToken cancellationToken = default) =>
      UpdateManyAsync(
          keys,
          "deleted_at = NULL, updated_at = $timestamp",
          "deleted_at IS NOT NULL",
          value: null,
          cancellationToken);

  public async Task<PurgeResult> PurgeAsync(
      IEnumerable<PostKey> keys,
      CancellationToken cancellationToken = default)
  {
    var normalized = NormalizeKeys(keys);
    if (normalized.Count == 0)
    {
      return new PurgeResult(0, Array.Empty<string>());
    }

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = connection.BeginTransaction(deferred: false);
    var (identity, parameters) = BuildIdentityPredicate(normalized);
    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    await using (var select = connection.CreateCommand())
    {
      select.Transaction = transaction;
      select.CommandText = $"""
                SELECT i.local_path, i.thumbnail_path
                FROM post_images i
                JOIN posts p ON p.row_id = i.post_id
                WHERE p.deleted_at IS NOT NULL AND ({identity});
                """;
      AddParameters(select, parameters);
      await using var reader = await select.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
      {
        if (!reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0)))
        {
          paths.Add(reader.GetString(0));
        }
        if (!reader.IsDBNull(1) && !string.IsNullOrWhiteSpace(reader.GetString(1)))
        {
          paths.Add(reader.GetString(1));
        }
      }
    }

    int purged;
    await using (var delete = connection.CreateCommand())
    {
      delete.Transaction = transaction;
      delete.CommandText = $"DELETE FROM posts AS p WHERE deleted_at IS NOT NULL AND ({identity});";
      AddParameters(delete, parameters);
      purged = await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var orphanTags = connection.CreateCommand())
    {
      orphanTags.Transaction = transaction;
      orphanTags.CommandText = "DELETE FROM tags WHERE NOT EXISTS (SELECT 1 FROM post_tags WHERE post_tags.tag_id = tags.tag_id);";
      await orphanTags.ExecuteNonQueryAsync(cancellationToken);
    }

    await transaction.CommitAsync(cancellationToken);
    return new PurgeResult(purged, paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
  }

  private async Task<int> UpdateManyAsync(
      IEnumerable<PostKey> keys,
      string assignments,
      string additionalCondition,
      object? value,
      CancellationToken cancellationToken)
  {
    var normalized = NormalizeKeys(keys);
    if (normalized.Count == 0)
    {
      return 0;
    }

    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = connection.BeginTransaction(deferred: false);
    var updated = 0;
    foreach (var chunk in normalized.Chunk(300))
    {
      var (identity, parameters) = BuildIdentityPredicate(chunk);
      await using var command = connection.CreateCommand();
      command.Transaction = transaction;
      var condition = string.IsNullOrEmpty(additionalCondition)
          ? identity
          : $"({identity}) AND {additionalCondition}";
      command.CommandText = $"UPDATE posts AS p SET {assignments} WHERE {condition};";
      AddParameters(command, parameters);
      command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O"));
      if (value is not null)
      {
        command.Parameters.AddWithValue("$value", value);
      }
      updated += await command.ExecuteNonQueryAsync(cancellationToken);
    }

    await transaction.CommitAsync(cancellationToken);
    return updated;
  }

  private static IReadOnlyList<PostKey> NormalizeKeys(IEnumerable<PostKey> keys)
  {
    ArgumentNullException.ThrowIfNull(keys);
    var normalized = keys.Distinct().ToArray();
    foreach (var key in normalized)
    {
      ValidateKey(key);
    }
    return normalized;
  }

  private static (string Sql, Dictionary<string, object> Parameters) BuildIdentityPredicate(
      IEnumerable<PostKey> keys)
  {
    var conditions = new List<string>();
    var parameters = new Dictionary<string, object>(StringComparer.Ordinal);
    var index = 0;
    foreach (var key in keys)
    {
      var site = $"$site_{index}";
      var thread = $"$thread_{index}";
      conditions.Add($"(p.site_family = {site} AND p.thread_id = {thread})");
      parameters[site] = key.SiteFamily;
      parameters[thread] = key.ThreadId;
      index++;
    }
    return (string.Join(" OR ", conditions), parameters);
  }

  private static (string Sql, Dictionary<string, object> Parameters) BuildQueryWhere(PostQuery query)
  {
    var conditions = new List<string>();
    var parameters = new Dictionary<string, object>(StringComparer.Ordinal);
    if (query.OnlyTrashed)
    {
      conditions.Add("p.deleted_at IS NOT NULL");
    }
    else if (!query.IncludeTrashed)
    {
      conditions.Add("p.deleted_at IS NULL");
    }

    if (!string.IsNullOrWhiteSpace(query.ProjectType))
    {
      conditions.Add("p.project_type = $project_type");
      parameters["$project_type"] = query.ProjectType.Trim();
    }
    if (query.Status is not null)
    {
      conditions.Add("p.status = $status");
      parameters["$status"] = query.Status.Value.ToStorageValue();
    }
    if (query.OnlyStarred)
    {
      conditions.Add("p.is_starred = 1");
    }
    if (!string.IsNullOrWhiteSpace(query.Tag))
    {
      conditions.Add("""
                EXISTS (
                    SELECT 1 FROM post_tags pt
                    JOIN tags t ON t.tag_id = pt.tag_id
                    WHERE pt.post_id = p.row_id AND t.name = $tag COLLATE NOCASE)
                """);
      parameters["$tag"] = query.Tag.Trim();
    }
    if (!string.IsNullOrWhiteSpace(query.Search))
    {
      conditions.Add("""
                (
                    p.title LIKE $search ESCAPE '\' COLLATE NOCASE OR
                    coalesce(p.extract_password, '') LIKE $search ESCAPE '\' COLLATE NOCASE OR
                    coalesce(p.body_text, '') LIKE $search ESCAPE '\' COLLATE NOCASE OR
                    p.canonical_url LIKE $search ESCAPE '\' COLLATE NOCASE OR
                    coalesce(p.note, '') LIKE $search ESCAPE '\' COLLATE NOCASE OR
                    EXISTS (
                        SELECT 1 FROM resource_links rl
                        WHERE rl.post_id = p.row_id AND (
                            rl.url LIKE $search ESCAPE '\' COLLATE NOCASE OR
                            rl.label LIKE $search ESCAPE '\' COLLATE NOCASE OR
                            rl.context_text LIKE $search ESCAPE '\' COLLATE NOCASE)) OR
                    EXISTS (
                        SELECT 1 FROM post_tags pt
                        JOIN tags t ON t.tag_id = pt.tag_id
                        WHERE pt.post_id = p.row_id AND t.name LIKE $search ESCAPE '\' COLLATE NOCASE)
                )
                """);
      parameters["$search"] = $"%{EscapeLike(query.Search.Trim())}%";
    }

    return (conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions), parameters);
  }

  private static string EscapeLike(string value) =>
      value.Replace("\\", "\\\\", StringComparison.Ordinal)
          .Replace("%", "\\%", StringComparison.Ordinal)
          .Replace("_", "\\_", StringComparison.Ordinal);

  private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object> parameters)
  {
    foreach (var parameter in parameters)
    {
      command.Parameters.AddWithValue(parameter.Key, parameter.Value);
    }
  }

  private static async Task<IReadOnlyList<StoredPost>> HydratePostsAsync(
      SqliteConnection connection,
      IReadOnlyList<StoredPost> posts,
      CancellationToken cancellationToken)
  {
    if (posts.Count == 0)
    {
      return posts;
    }

    var ids = posts.Select(post => post.RowId).ToArray();
    var parameterNames = ids.Select((_, index) => $"$post_{index}").ToArray();
    var inClause = string.Join(", ", parameterNames);
    var images = ids.ToDictionary(id => id, _ => new List<CapturedPostImage>());
    var links = ids.ToDictionary(id => id, _ => new List<CapturedResourceLink>());
    var tags = ids.ToDictionary(id => id, _ => new List<string>());

    await using (var command = connection.CreateCommand())
    {
      command.CommandText = $"""
                WITH current_generation(post_id, value) AS (
                  SELECT requested.post_id, COALESCE(MAX(s.capture_generation), 0)
                  FROM (
                    SELECT row_id AS post_id FROM posts WHERE row_id IN ({inClause})
                  ) requested
                  LEFT JOIN capture_snapshots s ON s.post_id = requested.post_id
                  GROUP BY requested.post_id
                ), effective_images AS (
                  SELECT i.image_id, i.post_id, i.position, i.source_url, i.local_path,
                         i.thumbnail_path, i.download_status, i.capture_generation,
                         current_generation.value AS current_generation,
                         ROW_NUMBER() OVER (
                           PARTITION BY i.post_id, i.position
                           ORDER BY
                             CASE
                               WHEN i.capture_generation = current_generation.value
                                AND i.download_status = 'downloaded'
                                AND NULLIF(TRIM(i.local_path), '') IS NOT NULL THEN 0
                               WHEN i.capture_generation < current_generation.value THEN 1
                               ELSE 2
                             END,
                             i.capture_generation DESC,
                             i.image_id DESC) AS choice
                  FROM post_images i
                  JOIN current_generation ON current_generation.post_id = i.post_id
                  WHERE i.capture_generation = current_generation.value
                     OR (
                       i.capture_generation < current_generation.value
                       AND i.download_status = 'downloaded'
                       AND NULLIF(TRIM(i.local_path), '') IS NOT NULL
                       AND EXISTS (
                         SELECT 1 FROM post_images current_image
                         WHERE current_image.post_id = i.post_id
                           AND current_image.capture_generation = current_generation.value
                           AND current_image.position = i.position)
                     )
                )
                SELECT i.post_id, i.position, i.source_url, i.local_path,
                       i.thumbnail_path, i.download_status, i.capture_generation,
                       CASE WHEN i.capture_generation = i.current_generation THEN 0 ELSE 1 END
                FROM effective_images i
                WHERE i.choice = 1
                ORDER BY i.post_id, i.position;
                """;
      AddIdParameters(command, parameterNames, ids);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
      {
        images[reader.GetInt64(0)].Add(new CapturedPostImage(
            reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7) != 0));
      }
    }

    await using (var command = connection.CreateCommand())
    {
      command.CommandText = $"""
                SELECT post_id, position, url, label, context_text
                FROM resource_links WHERE post_id IN ({inClause}) ORDER BY post_id, position;
                """;
      AddIdParameters(command, parameterNames, ids);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
      {
        links[reader.GetInt64(0)].Add(new CapturedResourceLink(
            reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
      }
    }

    await using (var command = connection.CreateCommand())
    {
      command.CommandText = $"""
                SELECT pt.post_id, t.name
                FROM post_tags pt JOIN tags t ON t.tag_id = pt.tag_id
                WHERE pt.post_id IN ({inClause}) ORDER BY pt.post_id, t.name COLLATE NOCASE;
                """;
      AddIdParameters(command, parameterNames, ids);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
      {
        tags[reader.GetInt64(0)].Add(reader.GetString(1));
      }
    }

    return posts.Select(post => post with
    {
      Images = images[post.RowId],
      ResourceLinks = links[post.RowId],
      Tags = tags[post.RowId]
    }).ToArray();
  }

  private static void AddIdParameters(SqliteCommand command, string[] names, long[] ids)
  {
    for (var index = 0; index < names.Length; index++)
    {
      command.Parameters.AddWithValue(names[index], ids[index]);
    }
  }

  private async Task<bool> UpdateManualColumnAsync(
      PostKey key,
      string column,
      object value,
      CancellationToken cancellationToken)
  {
    ValidateKey(key);
    await using var connection = _database.CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
            UPDATE posts
            SET {column} = $value, updated_at = $updated_at
            WHERE site_family = $site_family AND thread_id = $thread_id;
            """;
    command.Parameters.AddWithValue("$value", value);
    command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
    command.Parameters.AddWithValue("$site_family", key.SiteFamily);
    command.Parameters.AddWithValue("$thread_id", key.ThreadId);
    return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
  }

  private static async Task<long?> FindRowIdAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      PostKey key,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
            SELECT row_id FROM posts
            WHERE site_family = $site_family AND thread_id = $thread_id;
            """;
    command.Parameters.AddWithValue("$site_family", key.SiteFamily);
    command.Parameters.AddWithValue("$thread_id", key.ThreadId);
    var value = await command.ExecuteScalarAsync(cancellationToken);
    return value is null or DBNull ? null : Convert.ToInt64(value);
  }

  private static bool IsActiveRunState(string state) => state is
      "Preparing" or "WaitingForUser" or "Discovering" or "Collecting" or "CancelRequested";

  private static async Task EnsureCaptureWorkExistsAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      CaptureSnapshotMetadata snapshot,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
            SELECT count(*)
            FROM collection_work_items w
            JOIN collection_runs r ON r.id = w.run_id
            WHERE w.run_id = $run_id
              AND w.logical_key = $logical_key
              AND w.state <> 'committed'
              AND r.state IN ('Preparing', 'WaitingForUser', 'Discovering', 'Collecting', 'CancelRequested');
            """;
    command.Parameters.AddWithValue("$run_id", snapshot.RunId);
    command.Parameters.AddWithValue("$logical_key", snapshot.WorkItemKey);
    if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
    {
      throw new InvalidOperationException(
          "The collection work item is missing, committed, or belongs to an inactive run.");
    }
  }

  private static async Task<(long SnapshotId, long CaptureGeneration, bool IsNew)> UpsertSnapshotAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      long postId,
      CaptureSnapshotMetadata snapshot,
      int expectedImageCount,
      CancellationToken cancellationToken)
  {
    await using (var existing = connection.CreateCommand())
    {
      existing.Transaction = transaction;
      existing.CommandText = """
              SELECT snapshot_id, capture_generation, post_id, expected_image_count
              FROM capture_snapshots
              WHERE run_id = $run_id
                AND work_item_key = $work_item_key
                AND sha256 = $sha256;
              """;
      existing.Parameters.AddWithValue("$run_id", snapshot.RunId);
      existing.Parameters.AddWithValue("$work_item_key", snapshot.WorkItemKey);
      existing.Parameters.AddWithValue("$sha256", snapshot.Sha256.ToLowerInvariant());
      await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
      if (await reader.ReadAsync(cancellationToken))
      {
        var snapshotId = reader.GetInt64(0);
        var generation = reader.GetInt64(1);
        var storedPostId = reader.GetInt64(2);
        var storedExpectedImageCount = reader.GetInt32(3);
        await reader.DisposeAsync();
        if (storedPostId != postId || storedExpectedImageCount != expectedImageCount)
        {
          throw new InvalidOperationException(
              "An existing capture snapshot cannot be rebound to a different post or image manifest.");
        }
        await UpdateSnapshotAsync(
            connection,
            transaction,
            snapshotId,
            postId,
            snapshot,
            cancellationToken);
        return (snapshotId, generation, false);
      }
    }

    long captureGeneration;
    await using (var generation = connection.CreateCommand())
    {
      generation.Transaction = transaction;
      generation.CommandText = """
              SELECT COALESCE(MAX(capture_generation), 0) + 1
              FROM capture_snapshots
              WHERE post_id = $post_id;
              """;
      generation.Parameters.AddWithValue("$post_id", postId);
      captureGeneration = Convert.ToInt64(
          await generation.ExecuteScalarAsync(cancellationToken));
    }

    await using (var insert = connection.CreateCommand())
    {
      insert.Transaction = transaction;
      insert.CommandText = """
                INSERT INTO capture_snapshots (
                    run_id, work_item_key, post_id, requested_url, final_url,
                    kind, sha256, captured_at, byte_count,
                    capture_generation, expected_image_count)
                VALUES (
                    $run_id, $work_item_key, $post_id, $requested_url, $final_url,
                    $kind, $sha256, $captured_at, $byte_count,
                    $capture_generation, $expected_image_count);
                """;
      AddSnapshotParameters(insert, postId, snapshot);
      insert.Parameters.AddWithValue("$capture_generation", captureGeneration);
      insert.Parameters.AddWithValue("$expected_image_count", expectedImageCount);
      await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var inserted = connection.CreateCommand();
    inserted.Transaction = transaction;
    inserted.CommandText = "SELECT last_insert_rowid();";
    return (
        Convert.ToInt64(await inserted.ExecuteScalarAsync(cancellationToken)),
        captureGeneration,
        true);
  }

  private static async Task UpdateSnapshotAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      long snapshotId,
      long postId,
      CaptureSnapshotMetadata snapshot,
      CancellationToken cancellationToken)
  {
    await using var update = connection.CreateCommand();
    update.Transaction = transaction;
    update.CommandText = """
            UPDATE capture_snapshots
            SET post_id = $post_id,
                requested_url = $requested_url,
                final_url = $final_url,
                kind = $kind,
                captured_at = $captured_at,
                byte_count = $byte_count
            WHERE snapshot_id = $snapshot_id;
            """;
    AddSnapshotParameters(update, postId, snapshot);
    update.Parameters.AddWithValue("$snapshot_id", snapshotId);
    await update.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task EnsureExpectedImagesAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      long snapshotId,
      IReadOnlyList<CapturedPostImage> images,
      bool isNewSnapshot,
      CancellationToken cancellationToken)
  {
    if (isNewSnapshot)
    {
      foreach (var image in images.OrderBy(image => image.Position))
      {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                INSERT INTO capture_expected_images (snapshot_id, position, source_url)
                VALUES ($snapshot_id, $position, $source_url);
                """;
        insert.Parameters.AddWithValue("$snapshot_id", snapshotId);
        insert.Parameters.AddWithValue("$position", image.Position);
        insert.Parameters.AddWithValue("$source_url", image.SourceUrl);
        await insert.ExecuteNonQueryAsync(cancellationToken);
      }

      return;
    }

    var expected = images
        .OrderBy(image => image.Position)
        .Select(image => (image.Position, image.SourceUrl))
        .ToArray();
    var stored = new List<(int Position, string SourceUrl)>();
    await using var select = connection.CreateCommand();
    select.Transaction = transaction;
    select.CommandText = """
            SELECT position, source_url
            FROM capture_expected_images
            WHERE snapshot_id = $snapshot_id
            ORDER BY position;
            """;
    select.Parameters.AddWithValue("$snapshot_id", snapshotId);
    await using var reader = await select.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      stored.Add((reader.GetInt32(0), reader.GetString(1)));
    }

    if (!stored.SequenceEqual(expected))
    {
      throw new InvalidOperationException(
          "An existing capture snapshot has a different expected image manifest.");
    }
  }

  private static void AddSnapshotParameters(
      SqliteCommand command,
      long postId,
      CaptureSnapshotMetadata snapshot)
  {
    command.Parameters.AddWithValue("$run_id", snapshot.RunId);
    command.Parameters.AddWithValue("$work_item_key", snapshot.WorkItemKey);
    command.Parameters.AddWithValue("$post_id", postId);
    command.Parameters.AddWithValue("$requested_url", snapshot.RequestedUrl);
    command.Parameters.AddWithValue("$final_url", snapshot.FinalUrl);
    command.Parameters.AddWithValue("$kind", snapshot.Kind.Trim());
    command.Parameters.AddWithValue("$sha256", snapshot.Sha256.ToLowerInvariant());
    command.Parameters.AddWithValue("$captured_at", snapshot.CapturedAt.ToUniversalTime().ToString("O"));
    command.Parameters.AddWithValue("$byte_count", snapshot.ByteCount);
  }

  private static async Task<long> InsertCaptureAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      PostCapture capture,
      string timestamp,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
            INSERT INTO posts (
                site_family, thread_id, canonical_url, raw_url, mirror_url, favorite_url,
                title, project_type, status, favorite_time_raw, favorite_time_utc,
                download_count, visit_count, favorite_count, extract_password, body_text,
                created_at, updated_at, last_crawled_at)
            VALUES (
                $site_family, $thread_id, $canonical_url, $raw_url, $mirror_url, $favorite_url,
                $title, $project_type, 'not_downloaded', $favorite_time_raw, $favorite_time_utc,
                $download_count, $visit_count, $favorite_count, $extract_password, $body_text,
                $timestamp, $timestamp, $timestamp);
            SELECT last_insert_rowid();
            """;
    command.Parameters.AddWithValue("$site_family", capture.Key.SiteFamily);
    command.Parameters.AddWithValue("$thread_id", capture.Key.ThreadId);
    command.Parameters.AddWithValue("$canonical_url", capture.CanonicalUrl);
    command.Parameters.AddWithValue("$raw_url", DbValue(capture.RawUrl.IsObserved ? capture.RawUrl.Value : capture.CanonicalUrl));
    command.Parameters.AddWithValue("$mirror_url", DbValue(capture.MirrorUrl.Value));
    command.Parameters.AddWithValue("$favorite_url", DbValue(capture.FavoriteUrl.Value));
    command.Parameters.AddWithValue("$title", capture.Title.Value!.Trim());
    command.Parameters.AddWithValue("$project_type", NormalizeProjectType(capture.ProjectType.Value));
    command.Parameters.AddWithValue("$favorite_time_raw", DbValue(capture.FavoriteTimeRaw.Value));
    command.Parameters.AddWithValue("$favorite_time_utc", DbValue(FormatTimestamp(capture.FavoriteTimeUtc.Value)));
    command.Parameters.AddWithValue("$download_count", DbValue(capture.DownloadCount.Value));
    command.Parameters.AddWithValue("$visit_count", DbValue(capture.VisitCount.Value));
    command.Parameters.AddWithValue("$favorite_count", DbValue(capture.FavoriteCount.Value));
    command.Parameters.AddWithValue("$extract_password", DbValue(capture.ExtractPassword.Value));
    command.Parameters.AddWithValue("$body_text", DbValue(capture.BodyText.Value));
    command.Parameters.AddWithValue("$timestamp", timestamp);
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
  }

  private static async Task UpdateCaptureAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      long rowId,
      PostCapture capture,
      string timestamp,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    var assignments = new List<string>
        {
            "canonical_url = $canonical_url",
            "title = $title",
            "updated_at = $timestamp",
            "last_crawled_at = $timestamp"
        };
    command.Parameters.AddWithValue("$row_id", rowId);
    command.Parameters.AddWithValue("$canonical_url", capture.CanonicalUrl);
    command.Parameters.AddWithValue("$title", capture.Title.Value!.Trim());
    command.Parameters.AddWithValue("$timestamp", timestamp);

    AddObserved(assignments, command, "raw_url", capture.RawUrl.IsObserved, capture.RawUrl.Value);
    AddObserved(assignments, command, "mirror_url", capture.MirrorUrl.IsObserved, capture.MirrorUrl.Value);
    AddObserved(assignments, command, "favorite_url", capture.FavoriteUrl.IsObserved, capture.FavoriteUrl.Value);
    AddObserved(
        assignments,
        command,
        "project_type",
        capture.ProjectType.IsObserved,
        NormalizeProjectType(capture.ProjectType.Value));
    AddObserved(assignments, command, "favorite_time_raw", capture.FavoriteTimeRaw.IsObserved, capture.FavoriteTimeRaw.Value);
    AddObserved(
        assignments,
        command,
        "favorite_time_utc",
        capture.FavoriteTimeUtc.IsObserved,
        FormatTimestamp(capture.FavoriteTimeUtc.Value));
    AddObserved(assignments, command, "download_count", capture.DownloadCount.IsObserved, capture.DownloadCount.Value);
    AddObserved(assignments, command, "visit_count", capture.VisitCount.IsObserved, capture.VisitCount.Value);
    AddObserved(assignments, command, "favorite_count", capture.FavoriteCount.IsObserved, capture.FavoriteCount.Value);
    AddObserved(assignments, command, "extract_password", capture.ExtractPassword.IsObserved, capture.ExtractPassword.Value);
    AddObserved(assignments, command, "body_text", capture.BodyText.IsObserved, capture.BodyText.Value);

    command.CommandText = $"UPDATE posts SET {string.Join(", ", assignments)} WHERE row_id = $row_id;";
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private static void AddObserved(
      ICollection<string> assignments,
      SqliteCommand command,
      string column,
      bool isObserved,
      object? value)
  {
    if (!isObserved)
    {
      return;
    }

    var parameter = "$" + column;
    assignments.Add($"{column} = {parameter}");
    command.Parameters.AddWithValue(parameter, DbValue(value));
  }

  private static async Task ReplaceImagesAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      long rowId,
      long captureGeneration,
      IReadOnlyList<CapturedPostImage> images,
      CancellationToken cancellationToken)
  {
    ValidateImages(images);
    var existing = new Dictionary<string, CapturedPostImage>(StringComparer.Ordinal);
    await using (var select = connection.CreateCommand())
    {
      select.Transaction = transaction;
      select.CommandText = """
                SELECT position, source_url, local_path, thumbnail_path, download_status,
                       capture_generation, 0
                FROM post_images
                WHERE post_id = $post_id
                ORDER BY capture_generation DESC;
                """;
      select.Parameters.AddWithValue("$post_id", rowId);
      await using var reader = await select.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
      {
        var image = ReadImage(reader);
        existing.TryAdd(image.SourceUrl, image);
      }
    }

    await using (var delete = connection.CreateCommand())
    {
      delete.Transaction = transaction;
      delete.CommandText = """
              DELETE FROM post_images
              WHERE post_id = $post_id AND capture_generation = $capture_generation;
              """;
      delete.Parameters.AddWithValue("$post_id", rowId);
      delete.Parameters.AddWithValue("$capture_generation", captureGeneration);
      await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    foreach (var image in images.OrderBy(image => image.Position))
    {
      existing.TryGetValue(image.SourceUrl, out var previous);
      var previousSucceeded = IsSuccessfulImage(previous);
      var incomingSucceeded = IsSuccessfulImage(image);
      var keepPrevious = previousSucceeded && !incomingSucceeded;
      var localPath = keepPrevious
          ? previous!.LocalPath
          : NonBlankOrNull(image.LocalPath) ?? previous?.LocalPath;
      var thumbnailPath = keepPrevious
          ? previous!.ThumbnailPath
          : NonBlankOrNull(image.ThumbnailPath) ?? previous?.ThumbnailPath;
      var downloadStatus = keepPrevious ? previous!.DownloadStatus : image.DownloadStatus;

      await using var insert = connection.CreateCommand();
      insert.Transaction = transaction;
      insert.CommandText = """
                INSERT INTO post_images (
                    post_id, capture_generation, position, source_url,
                    local_path, thumbnail_path, download_status)
                VALUES (
                    $post_id, $capture_generation, $position, $source_url,
                    $local_path, $thumbnail_path, $download_status);
                """;
      insert.Parameters.AddWithValue("$post_id", rowId);
      insert.Parameters.AddWithValue("$capture_generation", captureGeneration);
      insert.Parameters.AddWithValue("$position", image.Position);
      insert.Parameters.AddWithValue("$source_url", image.SourceUrl);
      insert.Parameters.AddWithValue("$local_path", DbValue(localPath));
      insert.Parameters.AddWithValue("$thumbnail_path", DbValue(thumbnailPath));
      insert.Parameters.AddWithValue("$download_status", downloadStatus);
      await insert.ExecuteNonQueryAsync(cancellationToken);
    }
  }

  private static async Task<IReadOnlyList<CapturedPostImage>> ReadCurrentGenerationImagesAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      long rowId,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
            SELECT i.position, i.source_url, i.local_path, i.thumbnail_path,
                   i.download_status, i.capture_generation, 0
            FROM post_images i
            WHERE i.post_id = $post_id
              AND i.capture_generation = COALESCE((
                SELECT MAX(s.capture_generation)
                FROM capture_snapshots s
                WHERE s.post_id = $post_id), 0)
            ORDER BY i.position;
            """;
    command.Parameters.AddWithValue("$post_id", rowId);
    var images = new List<CapturedPostImage>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      images.Add(ReadImage(reader));
    }

    return images;
  }

  private static bool IsSuccessfulImage(CapturedPostImage? image) =>
      image?.DownloadStatus == "downloaded" && !string.IsNullOrWhiteSpace(image.LocalPath);

  private static string? NonBlankOrNull(string? value) =>
      string.IsNullOrWhiteSpace(value) ? null : value;

  private static async Task ReplaceResourceLinksAsync(
      SqliteConnection connection,
      SqliteTransaction transaction,
      long rowId,
      IReadOnlyList<CapturedResourceLink> links,
      CancellationToken cancellationToken)
  {
    ValidateResourceLinks(links);
    await using (var delete = connection.CreateCommand())
    {
      delete.Transaction = transaction;
      delete.CommandText = "DELETE FROM resource_links WHERE post_id = $post_id;";
      delete.Parameters.AddWithValue("$post_id", rowId);
      await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    foreach (var link in links.OrderBy(link => link.Position))
    {
      await using var insert = connection.CreateCommand();
      insert.Transaction = transaction;
      insert.CommandText = """
                INSERT INTO resource_links (post_id, position, url, label, context_text)
                VALUES ($post_id, $position, $url, $label, $context_text);
                """;
      insert.Parameters.AddWithValue("$post_id", rowId);
      insert.Parameters.AddWithValue("$position", link.Position);
      insert.Parameters.AddWithValue("$url", link.Url);
      insert.Parameters.AddWithValue("$label", link.Label ?? string.Empty);
      insert.Parameters.AddWithValue("$context_text", link.ContextText ?? string.Empty);
      await insert.ExecuteNonQueryAsync(cancellationToken);
    }
  }

  private static async Task<IReadOnlyList<CapturedPostImage>> ReadImagesAsync(
      SqliteConnection connection,
      long rowId,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
            WITH current_generation(value) AS (
              SELECT COALESCE(MAX(capture_generation), 0)
              FROM capture_snapshots
              WHERE post_id = $post_id
            ), effective_images AS (
              SELECT i.image_id, i.position, i.source_url, i.local_path,
                     i.thumbnail_path, i.download_status, i.capture_generation,
                     current_generation.value AS current_generation,
                     ROW_NUMBER() OVER (
                       PARTITION BY i.position
                       ORDER BY
                         CASE
                           WHEN i.capture_generation = current_generation.value
                            AND i.download_status = 'downloaded'
                            AND NULLIF(TRIM(i.local_path), '') IS NOT NULL THEN 0
                           WHEN i.capture_generation < current_generation.value THEN 1
                           ELSE 2
                         END,
                         i.capture_generation DESC,
                         i.image_id DESC) AS choice
              FROM post_images i
              CROSS JOIN current_generation
              WHERE i.post_id = $post_id
                AND (
                  i.capture_generation = current_generation.value
                  OR (
                    i.capture_generation < current_generation.value
                    AND i.download_status = 'downloaded'
                    AND NULLIF(TRIM(i.local_path), '') IS NOT NULL
                    AND EXISTS (
                      SELECT 1 FROM post_images current_image
                      WHERE current_image.post_id = i.post_id
                        AND current_image.capture_generation = current_generation.value
                        AND current_image.position = i.position)
                  )
                )
            )
            SELECT i.position, i.source_url, i.local_path, i.thumbnail_path, i.download_status,
                   i.capture_generation,
                   CASE WHEN i.capture_generation = i.current_generation THEN 0 ELSE 1 END
            FROM effective_images i
            WHERE i.choice = 1
            ORDER BY i.position;
            """;
    command.Parameters.AddWithValue("$post_id", rowId);
    var images = new List<CapturedPostImage>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      images.Add(ReadImage(reader));
    }

    return images;
  }

  private static CapturedPostImage ReadImage(SqliteDataReader reader) => new(
      reader.GetInt32(0),
      reader.GetString(1),
      reader.IsDBNull(2) ? null : reader.GetString(2),
      reader.IsDBNull(3) ? null : reader.GetString(3),
      reader.GetString(4),
      reader.GetInt64(5),
      reader.GetInt32(6) != 0);

  private static async Task<IReadOnlyList<CapturedResourceLink>> ReadResourceLinksAsync(
      SqliteConnection connection,
      long rowId,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT position, url, label, context_text
            FROM resource_links WHERE post_id = $post_id ORDER BY position;
            """;
    command.Parameters.AddWithValue("$post_id", rowId);
    var links = new List<CapturedResourceLink>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      links.Add(new CapturedResourceLink(
          reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }

    return links;
  }

  private static async Task<IReadOnlyList<string>> ReadTagsAsync(
      SqliteConnection connection,
      long rowId,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT tags.name
            FROM tags
            JOIN post_tags ON post_tags.tag_id = tags.tag_id
            WHERE post_tags.post_id = $post_id
            ORDER BY tags.name COLLATE NOCASE;
            """;
    command.Parameters.AddWithValue("$post_id", rowId);
    var tags = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      tags.Add(reader.GetString(0));
    }

    return tags;
  }

  private static StoredPost ReadStoredPost(SqliteDataReader reader) => new(
      RowId: reader.GetInt64(0),
      Key: new PostKey(reader.GetString(1), reader.GetString(2)),
      CanonicalUrl: reader.GetString(3),
      RawUrl: reader.IsDBNull(4) ? null : reader.GetString(4),
      MirrorUrl: reader.IsDBNull(5) ? null : reader.GetString(5),
      FavoriteUrl: reader.IsDBNull(6) ? null : reader.GetString(6),
      Title: reader.GetString(7),
      ProjectType: reader.GetString(8),
      Status: PostStatusExtensions.FromStorageValue(reader.GetString(9)),
      FavoriteTimeRaw: reader.IsDBNull(10) ? null : reader.GetString(10),
      FavoriteTimeUtc: ParseTimestamp(reader, 11),
      DownloadCount: reader.IsDBNull(12) ? null : reader.GetInt32(12),
      VisitCount: reader.IsDBNull(13) ? null : reader.GetInt32(13),
      FavoriteCount: reader.IsDBNull(14) ? null : reader.GetInt32(14),
      ExtractPassword: reader.IsDBNull(15) ? null : reader.GetString(15),
      BodyText: reader.IsDBNull(16) ? null : reader.GetString(16),
      IsStarred: reader.GetInt32(17) != 0,
      Note: reader.GetString(18),
      DeletedAt: ParseTimestamp(reader, 19),
      CreatedAt: DateTimeOffset.Parse(reader.GetString(20)),
      UpdatedAt: DateTimeOffset.Parse(reader.GetString(21)),
      LastCrawledAt: DateTimeOffset.Parse(reader.GetString(22)),
      Images: Array.Empty<CapturedPostImage>(),
      ResourceLinks: Array.Empty<CapturedResourceLink>(),
      Tags: Array.Empty<string>());

  private static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal) =>
      reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

  private static string? FormatTimestamp(DateTimeOffset? value) =>
      value?.ToUniversalTime().ToString("O");

  private static object DbValue(object? value) => value ?? DBNull.Value;

  private static string NormalizeProjectType(string? value) =>
      string.IsNullOrWhiteSpace(value) ? "未分类" : value.Trim();

  private static void ValidateCapture(PostCapture capture)
  {
    ArgumentNullException.ThrowIfNull(capture);
    ValidateKey(capture.Key);
    ArgumentException.ThrowIfNullOrWhiteSpace(capture.CanonicalUrl);
    if (!Uri.TryCreate(capture.CanonicalUrl, UriKind.Absolute, out var canonicalUri) ||
        (canonicalUri.Scheme != Uri.UriSchemeHttp && canonicalUri.Scheme != Uri.UriSchemeHttps))
    {
      throw new ArgumentException("Canonical URL must be an absolute HTTP or HTTPS URL.", nameof(capture));
    }
    if (!capture.Title.IsObserved || string.IsNullOrWhiteSpace(capture.Title.Value))
    {
      throw new ArgumentException("A capture must contain an observed, non-empty title.", nameof(capture));
    }
  }

  private static void ValidateSnapshot(PostCapture capture, CaptureSnapshotMetadata snapshot)
  {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.RunId);
    ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.WorkItemKey);
    if (snapshot.PostKey != capture.Key)
    {
      throw new ArgumentException("Snapshot post identity must match the captured post.", nameof(snapshot));
    }
    if (!string.Equals(snapshot.WorkItemKey, capture.Key.ToString(), StringComparison.Ordinal))
    {
      throw new ArgumentException(
          "Snapshot work identity must match the captured post's stable key.",
          nameof(snapshot));
    }
    ValidateHttpUrl(snapshot.RequestedUrl, nameof(snapshot));
    ValidateHttpUrl(snapshot.FinalUrl, nameof(snapshot));
    ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Kind);
    if (snapshot.Sha256.Length != 64 || !snapshot.Sha256.All(Uri.IsHexDigit))
    {
      throw new ArgumentException("Snapshot SHA-256 must contain exactly 64 hexadecimal characters.", nameof(snapshot));
    }
    if (snapshot.CapturedAt == default)
    {
      throw new ArgumentException("Snapshot capture time is required.", nameof(snapshot));
    }
    if (snapshot.ByteCount < 0)
    {
      throw new ArgumentOutOfRangeException(nameof(snapshot), "Snapshot byte count cannot be negative.");
    }
  }

  private static void ValidateHttpUrl(string value, string parameterName)
  {
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
      throw new ArgumentException("URL must be an absolute HTTP or HTTPS URL.", parameterName);
    }
  }

  private static void ValidateKey(PostKey key)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(key.SiteFamily);
    ArgumentException.ThrowIfNullOrWhiteSpace(key.ThreadId);
  }

  private static void ValidateImages(IReadOnlyList<CapturedPostImage> images)
  {
    if (images.Any(image => image.Position <= 0 || string.IsNullOrWhiteSpace(image.SourceUrl)) ||
        images.Select(image => image.Position).Distinct().Count() != images.Count ||
        images.Select(image => image.SourceUrl).Distinct(StringComparer.Ordinal).Count() != images.Count)
    {
      throw new ArgumentException("Images require unique positive positions and unique non-empty source URLs.", nameof(images));
    }
  }

  private static void ValidateResourceLinks(IReadOnlyList<CapturedResourceLink> links)
  {
    if (links.Any(link => link.Position <= 0 || string.IsNullOrWhiteSpace(link.Url)) ||
        links.Select(link => link.Position).Distinct().Count() != links.Count)
    {
      throw new ArgumentException("Resource links require unique positive positions and non-empty URLs.", nameof(links));
    }
  }

  private static async Task<BookmarkPost?> GetAsync(
      SqliteConnection connection,
      string id,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT thread_id, canonical_url, title, project_type, status, favorite_time_utc
            FROM posts
            WHERE site_family = 'legacy' AND thread_id = $id;
            """;
    command.Parameters.AddWithValue("$id", id);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
      return null;
    }

    return ReadPost(reader);
  }

  private static void AddPostParameters(
      SqliteCommand command,
      BookmarkPost post,
      PostStatus status,
      string timestamp)
  {
    command.Parameters.AddWithValue("$id", post.Id);
    command.Parameters.AddWithValue("$url", post.Url);
    command.Parameters.AddWithValue("$title", post.Title);
    command.Parameters.AddWithValue("$type", post.Type);
    command.Parameters.AddWithValue("$status", status.ToStorageValue());
    command.Parameters.AddWithValue("$favorite_time", (object?)post.FavoriteTime?.ToString("O") ?? DBNull.Value);
    command.Parameters.AddWithValue("$created_at", timestamp);
    command.Parameters.AddWithValue("$updated_at", timestamp);
  }

  private static BookmarkPost ReadPost(SqliteDataReader reader)
  {
    return new BookmarkPost(
        Id: reader.GetString(0),
        Url: reader.GetString(1),
        Title: reader.GetString(2),
        Type: reader.GetString(3),
        Status: PostStatusExtensions.FromStorageValue(reader.GetString(4)),
        FavoriteTime: reader.IsDBNull(5)
            ? null
            : DateTimeOffset.Parse(reader.GetString(5)));
  }
}
