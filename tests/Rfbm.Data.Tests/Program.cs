using Rfbm.Core.Models;
using Rfbm.Data.Storage;
using Rfbm.Tests;

await DatabaseInitializationCreatesVersionedConfiguredSchema();
await MigrationFromVersionOneAddsCaptureSchema();
await MigrationFromVersionTwoPreservesCapturedImageManifest();
await MigrationFromProbeSchemaPreservesPosts();
await UpsertAsyncStoresAndReturnsPost();
await UpsertAsyncPreservesExistingManualStatus();
await ListAsyncSortsNewestFavoritesFirst();
await CaptureUpsertPersistsFullSnapshotAndChildren();
await RecrawlPreservesManualAndUnobservableFields();
await RecrawlKeepsDownloadedMediaForMatchingSource();
await SqlQueryCombinesSearchFiltersSortAndPaging();
await BulkStatusAndRecoverableTrashAreSafe();
await CollectionSchemaAllowsOnlyOneActiveRun();
await LegacyPythonImporterReadsSourceWithoutModifyingIt();
await CaptureRejectsNonWebIdentityBeforeMutation();
await ImageDownloadResultCanBeCommittedIndependently();
await CaptureAndWorkCheckpointCommitAtomically();
await FinalizeCaptureIsAtomicAndIdempotent();
await BoundFinalizeRejectsPendingOrMissingExpectedImages();
await BoundFinalizeRejectsStaleOrMismatchedCaptureAndCountsOnce();
await BoundImageUpdateRejectsStaleWorkerForSameSourceUrl();
await FailedMediaRefreshPreservesDownloadedFile();
await ChangedImageSourceStagesNewGenerationWithoutDroppingFallback();
await UnobservedImagesCarryForwardCurrentManifest();

Console.WriteLine("Rfbm.Data.Tests passed.");

static async Task DatabaseInitializationCreatesVersionedConfiguredSchema()
{
  var path = CreateDatabasePath();
  var database = new BookmarkDatabase(path);

  await database.InitializeAsync();

  await using var connection = database.CreateConnection();
  await connection.OpenAsync();
  TestAssert.Equal(3L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
  TestAssert.Equal("wal", (await ScalarStringAsync(connection, "PRAGMA journal_mode;")).ToLowerInvariant());
  TestAssert.Equal(1L, await ScalarInt64Async(connection, "PRAGMA foreign_keys;"));
  TestAssert.Equal(5000L, await ScalarInt64Async(connection, "PRAGMA busy_timeout;"));
  foreach (var table in new[]
           {
                 "schema_migrations", "posts", "post_images", "resource_links", "tags", "post_tags",
                 "collection_runs", "collection_frontier", "collection_work_items", "collection_events",
                 "capture_snapshots", "capture_expected_images"
             })
  {
    TestAssert.Equal(
        1L,
        await ScalarInt64Async(
            connection,
            $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';"));
  }
  TestAssert.Equal(
      1L,
      await ScalarInt64Async(
          connection,
          "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_posts_identity';"));
}

static async Task MigrationFromVersionOneAddsCaptureSchema()
{
  var path = CreateDatabasePath();
  await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
  {
    await connection.OpenAsync();
    await ExecuteAsync(connection, """
      PRAGMA user_version = 1;
      CREATE TABLE schema_migrations (
        version INTEGER PRIMARY KEY,
        name TEXT NOT NULL,
        applied_at TEXT NOT NULL
      );
      INSERT INTO schema_migrations VALUES (1, 'native_baseline', '2026-08-24T00:00:00Z');
      CREATE TABLE posts (
        row_id INTEGER PRIMARY KEY,
        site_family TEXT NOT NULL,
        thread_id TEXT NOT NULL
      );
      CREATE TABLE post_images (
        image_id INTEGER PRIMARY KEY,
        post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
        position INTEGER NOT NULL,
        source_url TEXT NOT NULL,
        local_path TEXT,
        thumbnail_path TEXT,
        download_status TEXT NOT NULL DEFAULT 'pending',
        UNIQUE(post_id, position),
        UNIQUE(post_id, source_url)
      );
      CREATE TABLE collection_runs (
        id TEXT PRIMARY KEY,
        state TEXT NOT NULL,
        plan_json TEXT NOT NULL,
        started_at TEXT NOT NULL,
        threads_committed INTEGER NOT NULL DEFAULT 0
      );
      CREATE TABLE collection_work_items (
        run_id TEXT NOT NULL,
        logical_key TEXT NOT NULL,
        uri TEXT NOT NULL,
        state TEXT NOT NULL,
        PRIMARY KEY (run_id, logical_key),
        FOREIGN KEY (run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
      );
      """);
  }

  var database = new BookmarkDatabase(path);
  await database.InitializeAsync();

  await using var verification = database.CreateConnection();
  await verification.OpenAsync();
  TestAssert.Equal(3L, await ScalarInt64Async(verification, "PRAGMA user_version;"));
  TestAssert.Equal("wal", (await ScalarStringAsync(verification, "PRAGMA journal_mode;")).ToLowerInvariant());
  TestAssert.Equal(
      1L,
      await ScalarInt64Async(
          verification,
          "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'capture_snapshots';"));
  foreach (var column in new[]
           {
             "post_id", "snapshot_id", "capture_generation", "media_pending",
             "work_parameters_json", "captured_at", "committed_at"
           })
  {
    TestAssert.Equal(
        1L,
        await ScalarInt64Async(
            verification,
            $"SELECT count(*) FROM pragma_table_info('collection_work_items') WHERE name = '{column}';"));
  }
}

static async Task MigrationFromVersionTwoPreservesCapturedImageManifest()
{
  var path = CreateDatabasePath();
  await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
  {
    await connection.OpenAsync();
    await ExecuteAsync(connection, """
      PRAGMA foreign_keys = ON;
      PRAGMA user_version = 2;
      CREATE TABLE schema_migrations (
        version INTEGER PRIMARY KEY,
        name TEXT NOT NULL,
        applied_at TEXT NOT NULL
      );
      INSERT INTO schema_migrations VALUES (1, 'native_baseline', '2026-08-24T00:00:00Z');
      INSERT INTO schema_migrations VALUES (2, 'durable_capture_checkpoint', '2026-08-24T00:01:00Z');
      CREATE TABLE posts (
        row_id INTEGER PRIMARY KEY,
        site_family TEXT NOT NULL,
        thread_id TEXT NOT NULL
      );
      CREATE TABLE post_images (
        image_id INTEGER PRIMARY KEY,
        post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
        position INTEGER NOT NULL,
        source_url TEXT NOT NULL,
        local_path TEXT,
        thumbnail_path TEXT,
        download_status TEXT NOT NULL DEFAULT 'pending',
        UNIQUE(post_id, position),
        UNIQUE(post_id, source_url)
      );
      CREATE TABLE collection_runs (
        id TEXT PRIMARY KEY,
        state TEXT NOT NULL,
        plan_json TEXT NOT NULL,
        started_at TEXT NOT NULL,
        threads_committed INTEGER NOT NULL DEFAULT 0
      );
      CREATE TABLE collection_work_items (
        run_id TEXT NOT NULL,
        logical_key TEXT NOT NULL,
        uri TEXT NOT NULL,
        state TEXT NOT NULL,
        post_id INTEGER REFERENCES posts(row_id) ON DELETE SET NULL,
        snapshot_id INTEGER REFERENCES capture_snapshots(snapshot_id) ON DELETE SET NULL,
        media_pending INTEGER NOT NULL DEFAULT 0,
        work_parameters_json TEXT,
        captured_at TEXT,
        committed_at TEXT,
        PRIMARY KEY (run_id, logical_key),
        FOREIGN KEY (run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
      );
      CREATE TABLE capture_snapshots (
        snapshot_id INTEGER PRIMARY KEY,
        run_id TEXT NOT NULL,
        work_item_key TEXT NOT NULL,
        post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
        requested_url TEXT NOT NULL,
        final_url TEXT NOT NULL,
        kind TEXT NOT NULL,
        sha256 TEXT NOT NULL,
        captured_at TEXT NOT NULL,
        byte_count INTEGER NOT NULL,
        UNIQUE(run_id, work_item_key, sha256),
        FOREIGN KEY (run_id, work_item_key)
          REFERENCES collection_work_items(run_id, logical_key) ON DELETE CASCADE
      );
      INSERT INTO posts VALUES (1, 'discuz.example', 'migrated-v2');
      INSERT INTO post_images VALUES (
        1, 1, 1, 'https://img.test/v2.jpg', 'images/v2.jpg', NULL, 'downloaded');
      INSERT INTO collection_runs VALUES ('v2-run', 'Collecting', '{}', '2026-08-24T00:00:00Z', 0);
      INSERT INTO collection_work_items (
        run_id, logical_key, uri, state, post_id, media_pending, captured_at)
      VALUES (
        'v2-run', 'discuz.example:migrated-v2', 'https://example.test/v2',
        'captured', 1, 0, '2026-08-24T00:02:00Z');
      INSERT INTO capture_snapshots VALUES (
        7, 'v2-run', 'discuz.example:migrated-v2', 1,
        'https://example.test/v2', 'https://example.test/v2', 'thread-html',
        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        '2026-08-24T00:02:00Z', 1024);
      UPDATE collection_work_items SET snapshot_id = 7 WHERE run_id = 'v2-run';
      """);
  }

  var database = new BookmarkDatabase(path);
  await database.InitializeAsync();
  await using var verify = database.CreateConnection();
  await verify.OpenAsync();
  TestAssert.Equal(3L, await ScalarInt64Async(verify, "PRAGMA user_version;"));
  TestAssert.Equal(
      "images/v2.jpg",
      await ScalarStringAsync(verify, "SELECT local_path FROM post_images WHERE image_id = 1;"));
  TestAssert.Equal(
      0L,
      await ScalarInt64Async(verify, "SELECT capture_generation FROM post_images WHERE image_id = 1;"));
  TestAssert.Equal(
      1L,
      await ScalarInt64Async(verify, "SELECT expected_image_count FROM capture_snapshots WHERE snapshot_id = 7;"));
  TestAssert.Equal(
      "https://img.test/v2.jpg",
      await ScalarStringAsync(verify, "SELECT source_url FROM capture_expected_images WHERE snapshot_id = 7;"));
}

static async Task MigrationFromProbeSchemaPreservesPosts()
{
  var path = CreateDatabasePath();
  await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
  {
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
            CREATE TABLE posts (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                title TEXT NOT NULL,
                type TEXT NOT NULL,
                status TEXT NOT NULL,
                favorite_time TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO posts (id, url, title, type, status, favorite_time, created_at, updated_at)
            VALUES (
                'thread-42', 'https://legacy.test/thread-42', '旧库收藏', '教程', 'downloaded',
                '2026-05-30T12:00:00.0000000+00:00',
                '2026-05-30T12:00:00.0000000+00:00', '2026-05-30T12:00:00.0000000+00:00');
            """;
    await command.ExecuteNonQueryAsync();
  }

  var database = new BookmarkDatabase(path);
  await database.InitializeAsync();

  var saved = TestAssert.NotNull(await new PostRepository(database).GetAsync("thread-42"));
  TestAssert.Equal("旧库收藏", saved.Title);
  TestAssert.Equal(PostStatus.Downloaded, saved.Status);
}

static async Task UpsertAsyncStoresAndReturnsPost()
{
  var repository = await CreateRepositoryAsync();
  var post = new BookmarkPost(
      Id: "post-1",
      Url: "https://example.test/thread-1",
      Title: "测试标题",
      Type: "合集",
      Status: PostStatus.NotDownloaded,
      FavoriteTime: new DateTimeOffset(2026, 5, 31, 10, 0, 0, TimeSpan.Zero));

  await repository.UpsertAsync(post);

  var saved = TestAssert.NotNull(await repository.GetAsync("post-1"));
  TestAssert.Equal("测试标题", saved.Title);
  TestAssert.Equal("合集", saved.Type);
  TestAssert.Equal(PostStatus.NotDownloaded, saved.Status);
}

static async Task UpsertAsyncPreservesExistingManualStatus()
{
  var repository = await CreateRepositoryAsync();
  var first = new BookmarkPost(
      Id: "post-1",
      Url: "https://example.test/thread-1",
      Title: "旧标题",
      Type: "合集",
      Status: PostStatus.NotDownloaded,
      FavoriteTime: null);
  var recrawled = first with { Title = "新标题", Status = PostStatus.NotDownloaded };

  await repository.UpsertAsync(first);
  await repository.SetStatusAsync("post-1", PostStatus.Downloaded);
  await repository.UpsertAsync(recrawled);

  var saved = TestAssert.NotNull(await repository.GetAsync("post-1"));
  TestAssert.Equal("新标题", saved.Title);
  TestAssert.Equal(PostStatus.Downloaded, saved.Status);
}

static async Task ListAsyncSortsNewestFavoritesFirst()
{
  var repository = await CreateRepositoryAsync();

  await repository.UpsertAsync(new BookmarkPost(
      Id: "old",
      Url: "https://example.test/old",
      Title: "旧收藏",
      Type: "图片",
      Status: PostStatus.NotDownloaded,
      FavoriteTime: new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero)));
  await repository.UpsertAsync(new BookmarkPost(
      Id: "new",
      Url: "https://example.test/new",
      Title: "新收藏",
      Type: "视频",
      Status: PostStatus.NotDownloaded,
      FavoriteTime: new DateTimeOffset(2026, 5, 31, 10, 0, 0, TimeSpan.Zero)));

  var posts = await repository.ListAsync();

  TestAssert.Equal("new", posts[0].Id);
  TestAssert.Equal("old", posts[1].Id);
}

static async Task CaptureUpsertPersistsFullSnapshotAndChildren()
{
  var repository = await CreateRepositoryAsync();
  var key = new PostKey("discuz.example", "12345");
  var favoriteTime = new DateTimeOffset(2026, 8, 24, 9, 30, 0, TimeSpan.FromHours(8));

  await repository.UpsertCaptureAsync(new PostCapture
  {
    Key = key,
    CanonicalUrl = "https://example.test/thread-12345-1-1.html",
    RawUrl = Observation<string?>.Observed("https://mirror.test/thread-12345-1-1.html"),
    MirrorUrl = Observation<string?>.Observed("https://mirror.test/thread-12345-1-1.html"),
    FavoriteUrl = Observation<string?>.Observed("https://example.test/home.php?mod=space&do=favorite"),
    Title = Observation<string>.Observed("完整收藏"),
    ProjectType = Observation<string>.Observed("教程"),
    FavoriteTimeRaw = Observation<string?>.Observed("昨天 09:30"),
    FavoriteTimeUtc = Observation<DateTimeOffset?>.Observed(favoriteTime),
    DownloadCount = Observation<int?>.Observed(12),
    VisitCount = Observation<int?>.Observed(345),
    FavoriteCount = Observation<int?>.Observed(67),
    ExtractPassword = Observation<string?>.Observed("2jsu"),
    BodyText = Observation<string?>.Observed("这里是正文摘要"),
    Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(
          new[] { new CapturedPostImage(1, "https://img.test/one.jpg") }),
    ResourceLinks = Observation<IReadOnlyList<CapturedResourceLink>>.Observed(
          new[] { new CapturedResourceLink(1, "https://pan.test/s/abc", "主程序", "提取码 2jsu") })
  });

  var saved = TestAssert.NotNull(await repository.GetAsync(key));
  TestAssert.Equal("完整收藏", saved.Title);
  TestAssert.Equal("昨天 09:30", saved.FavoriteTimeRaw);
  TestAssert.Equal(favoriteTime, saved.FavoriteTimeUtc);
  TestAssert.Equal(12, saved.DownloadCount);
  TestAssert.Equal("2jsu", saved.ExtractPassword);
  TestAssert.Equal(1, saved.Images.Count);
  TestAssert.Equal("https://img.test/one.jpg", saved.Images[0].SourceUrl);
  TestAssert.Equal(1, saved.ResourceLinks.Count);
  TestAssert.Equal("提取码 2jsu", saved.ResourceLinks[0].ContextText);
}

static async Task RecrawlPreservesManualAndUnobservableFields()
{
  var repository = await CreateRepositoryAsync();
  var key = new PostKey("discuz.example", "keep-manual");
  await repository.UpsertCaptureAsync(CreateCapture(
      key,
      "旧标题",
      body: Observation<string?>.Observed("旧正文"),
      images: Observation<IReadOnlyList<CapturedPostImage>>.Observed(
          new[] { new CapturedPostImage(1, "https://img.test/keep.jpg") }),
      links: Observation<IReadOnlyList<CapturedResourceLink>>.Observed(
          new[] { new CapturedResourceLink(1, "https://pan.test/old", "旧链接", "旧上下文") })));
  await repository.SetStatusAsync(key, PostStatus.Downloaded);
  await repository.SetStarredAsync(key, true);
  await repository.SetNoteAsync(key, "人工笔记");
  await repository.ReplaceTagsAsync(key, new[] { "精选", "待整理", "精选" });

  await repository.UpsertCaptureAsync(CreateCapture(
      key,
      "新标题",
      body: Observation<string?>.Unobserved,
      images: Observation<IReadOnlyList<CapturedPostImage>>.Unobserved,
      links: Observation<IReadOnlyList<CapturedResourceLink>>.Observed(Array.Empty<CapturedResourceLink>())));

  var saved = TestAssert.NotNull(await repository.GetAsync(key));
  TestAssert.Equal("新标题", saved.Title);
  TestAssert.Equal(PostStatus.Downloaded, saved.Status);
  TestAssert.Equal(true, saved.IsStarred);
  TestAssert.Equal("人工笔记", saved.Note);
  TestAssert.Equal("旧正文", saved.BodyText);
  TestAssert.Equal(1, saved.Images.Count);
  TestAssert.Equal(0, saved.ResourceLinks.Count);
  TestAssert.Equal(2, saved.Tags.Count);
  TestAssert.Equal("待整理", saved.Tags[0]);
  TestAssert.Equal("精选", saved.Tags[1]);
}

static async Task RecrawlKeepsDownloadedMediaForMatchingSource()
{
  var repository = await CreateRepositoryAsync();
  var key = new PostKey("discuz.example", "keep-image");
  await repository.UpsertCaptureAsync(CreateCapture(
      key,
      "图片",
      images: Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
      {
            new CapturedPostImage(
                1,
                "https://img.test/stable.jpg",
                LocalPath: "images/stable.jpg",
                ThumbnailPath: "images/stable.thumb.jpg",
                DownloadStatus: "downloaded")
      })));

  await repository.UpsertCaptureAsync(CreateCapture(
      key,
      "图片更新",
      images: Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
      {
            new CapturedPostImage(1, "https://img.test/new.jpg"),
            new CapturedPostImage(2, "https://img.test/stable.jpg")
      })));

  var saved = TestAssert.NotNull(await repository.GetAsync(key));
  TestAssert.Equal(2, saved.Images.Count);
  TestAssert.Equal("https://img.test/stable.jpg", saved.Images[1].SourceUrl);
  TestAssert.Equal("images/stable.jpg", saved.Images[1].LocalPath);
  TestAssert.Equal("downloaded", saved.Images[1].DownloadStatus);
}

static async Task SqlQueryCombinesSearchFiltersSortAndPaging()
{
  var repository = await CreateRepositoryAsync();
  var older = new PostKey("discuz.example", "query-old");
  var newer = new PostKey("discuz.example", "query-new");
  var unrelated = new PostKey("discuz.example", "query-other");

  await repository.UpsertCaptureAsync(CreateCapture(older, "旧教程") with
  {
    ProjectType = Observation<string>.Observed("教程"),
    FavoriteTimeUtc = Observation<DateTimeOffset?>.Observed(
          new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)),
    ResourceLinks = Observation<IReadOnlyList<CapturedResourceLink>>.Observed(new[]
      {
            new CapturedResourceLink(1, "https://pan.test/unique", "工具包", "唯一检索词 blue-orchid")
        })
  });
  await repository.UpsertCaptureAsync(CreateCapture(newer, "新教程") with
  {
    ProjectType = Observation<string>.Observed("教程"),
    FavoriteTimeUtc = Observation<DateTimeOffset?>.Observed(
          new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero))
  });
  await repository.UpsertCaptureAsync(CreateCapture(unrelated, "图片合集") with
  {
    ProjectType = Observation<string>.Observed("图片"),
    FavoriteTimeUtc = Observation<DateTimeOffset?>.Observed(
          new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero))
  });
  await repository.SetStatusManyAsync(new[] { older, newer }, PostStatus.Downloaded);

  var firstPage = await repository.QueryAsync(new PostQuery
  {
    ProjectType = "教程",
    Status = PostStatus.Downloaded,
    Sort = PostSort.NewestFavorite,
    Limit = 1
  });
  TestAssert.Equal(2, firstPage.Total);
  TestAssert.Equal(1, firstPage.Items.Count);
  TestAssert.Equal(newer, firstPage.Items[0].Key);

  var secondPage = await repository.QueryAsync(new PostQuery
  {
    ProjectType = "教程",
    Status = PostStatus.Downloaded,
    Sort = PostSort.NewestFavorite,
    Offset = 1,
    Limit = 1
  });
  TestAssert.Equal(older, secondPage.Items[0].Key);

  var searched = await repository.QueryAsync(new PostQuery { Search = "BLUE-ORCHID" });
  TestAssert.Equal(1, searched.Total);
  TestAssert.Equal(older, searched.Items[0].Key);
  TestAssert.Equal(1, searched.Items[0].ResourceLinks.Count);
}

static async Task BulkStatusAndRecoverableTrashAreSafe()
{
  var repository = await CreateRepositoryAsync();
  var first = new PostKey("discuz.example", "trash-1");
  var second = new PostKey("discuz.example", "trash-2");
  var mediaPath = Path.Combine(Path.GetTempPath(), "rfbm-tests", $"{Guid.NewGuid():N}.jpg");
  await File.WriteAllTextAsync(mediaPath, "not-real-image");
  await repository.UpsertCaptureAsync(CreateCapture(first, "待删除") with
  {
    Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
      {
            new CapturedPostImage(1, "https://img.test/trash.jpg", LocalPath: mediaPath, DownloadStatus: "downloaded")
        })
  });
  await repository.UpsertCaptureAsync(CreateCapture(second, "保留"));

  TestAssert.Equal(2, await repository.SetStatusManyAsync(new[] { first, second }, PostStatus.LinkInvalid));
  TestAssert.Equal(PostStatus.LinkInvalid, TestAssert.NotNull(await repository.GetAsync(first)).Status);
  TestAssert.Equal(0, (await repository.PurgeAsync(new[] { first })).PurgedCount);

  TestAssert.Equal(1, await repository.MoveToTrashAsync(new[] { first }));
  TestAssert.Equal<StoredPost?>(null, await repository.GetAsync(first));
  TestAssert.NotNull(await repository.GetAsync(first, includeDeleted: true));
  TestAssert.Equal(1, await repository.RestoreAsync(new[] { first }));
  TestAssert.NotNull(await repository.GetAsync(first));

  await repository.MoveToTrashAsync(new[] { first });
  var purged = await repository.PurgeAsync(new[] { first, second });
  TestAssert.Equal(1, purged.PurgedCount);
  TestAssert.Equal(1, purged.LocalMediaPaths.Count);
  TestAssert.Equal(mediaPath, purged.LocalMediaPaths[0]);
  TestAssert.Equal(true, File.Exists(mediaPath), "Database purge must not delete media files itself.");
  TestAssert.Equal<StoredPost?>(null, await repository.GetAsync(first, includeDeleted: true));
  TestAssert.NotNull(await repository.GetAsync(second));
  File.Delete(mediaPath);
}

static async Task CollectionSchemaAllowsOnlyOneActiveRun()
{
  var path = CreateDatabasePath();
  var database = new BookmarkDatabase(path);
  await database.InitializeAsync();
  await using var connection = database.CreateConnection();
  await connection.OpenAsync();

  await ExecuteAsync(
      connection,
      "INSERT INTO collection_runs (id, state, plan_json, started_at) VALUES ('run-1', 'Collecting', '{}', '2026-08-24T00:00:00Z');");
  await ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
      connection,
      "INSERT INTO collection_runs (id, state, plan_json, started_at) VALUES ('run-2', 'Preparing', '{}', '2026-08-24T00:00:01Z');"));
  await ExecuteAsync(connection, "UPDATE collection_runs SET state = 'Succeeded' WHERE id = 'run-1';");
  await ExecuteAsync(
      connection,
      "INSERT INTO collection_runs (id, state, plan_json, started_at) VALUES ('run-2', 'Preparing', '{}', '2026-08-24T00:00:01Z');");
}

static async Task LegacyPythonImporterReadsSourceWithoutModifyingIt()
{
  var sourcePath = CreateDatabasePath();
  await using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourcePath}"))
  {
    await source.OpenAsync();
    await ExecuteAsync(source, """
            PRAGMA user_version = 77;
            CREATE TABLE posts (
                id INTEGER PRIMARY KEY,
                post_url TEXT NOT NULL UNIQUE,
                favorite_url TEXT,
                title TEXT NOT NULL,
                project_type TEXT NOT NULL,
                status TEXT NOT NULL,
                favorite_time TEXT,
                download_count INTEGER,
                visit_count INTEGER,
                favorite_count INTEGER,
                extract_password TEXT,
                body_text TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_crawled_at TEXT
            );
            CREATE TABLE post_images (
                id INTEGER PRIMARY KEY,
                post_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                source_url TEXT NOT NULL,
                local_path TEXT,
                thumbnail_path TEXT,
                download_status TEXT NOT NULL
            );
            CREATE TABLE download_links (
                id INTEGER PRIMARY KEY,
                post_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                url TEXT NOT NULL,
                label TEXT,
                context_text TEXT
            );
            INSERT INTO posts VALUES (
                9, 'https://legacy.test/thread-900-1-1.html', 'https://legacy.test/favorites',
                '旧版帖子', '素材', '已下载', '2026-05-12', 4, 50, 6, 'abcd', '旧正文',
                '2026-05-12T00:00:00Z', '2026-05-13T00:00:00Z', '2026-05-13T00:00:00Z');
            INSERT INTO post_images VALUES (
                1, 9, 1, 'https://img.test/legacy.jpg', 'images/legacy.jpg', NULL, 'downloaded');
            INSERT INTO download_links VALUES (
                1, 9, 1, 'https://pan.test/legacy', '旧资源', '旧上下文');
            """);
  }

  var destination = new BookmarkDatabase(CreateDatabasePath());
  await destination.InitializeAsync();
  var key = new PostKey("legacy.test", "900");
  var result = await new LegacyPythonDatabaseImporter(destination).ImportAsync(sourcePath, _ => key);

  TestAssert.Equal(1, result.ImportedCount);
  var saved = TestAssert.NotNull(await new PostRepository(destination).GetAsync(key));
  TestAssert.Equal("旧版帖子", saved.Title);
  TestAssert.Equal(PostStatus.Downloaded, saved.Status);
  TestAssert.Equal(4, saved.DownloadCount);
  TestAssert.Equal(1, saved.Images.Count);
  TestAssert.Equal("images/legacy.jpg", saved.Images[0].LocalPath);
  TestAssert.Equal(1, saved.ResourceLinks.Count);

  await using var verification = new Microsoft.Data.Sqlite.SqliteConnection(
      new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
      {
        DataSource = sourcePath,
        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
      }.ToString());
  await verification.OpenAsync();
  TestAssert.Equal(77L, await ScalarInt64Async(verification, "PRAGMA user_version;"));
  TestAssert.Equal(1L, await ScalarInt64Async(verification, "SELECT count(*) FROM posts;"));
}

static async Task CaptureRejectsNonWebIdentityBeforeMutation()
{
  var repository = await CreateRepositoryAsync();
  var key = new PostKey("discuz.example", "valid-before-invalid");
  await repository.UpsertCaptureAsync(CreateCapture(key, "有效标题"));

  await ThrowsAsync<ArgumentException>(() => repository.UpsertCaptureAsync(CreateCapture(key, "错误页") with
  {
    CanonicalUrl = "about:blank",
    BodyText = Observation<string?>.Observed(null),
    Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(Array.Empty<CapturedPostImage>())
  }));

  var saved = TestAssert.NotNull(await repository.GetAsync(key));
  TestAssert.Equal("有效标题", saved.Title);
  TestAssert.Equal(true, saved.CanonicalUrl.StartsWith("https://", StringComparison.Ordinal));
}

static async Task CaptureAndWorkCheckpointCommitAtomically()
{
  var path = CreateDatabasePath();
  var database = new BookmarkDatabase(path);
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var key = new PostKey("discuz.example", "atomic-work");
  var missingRunId = Guid.NewGuid().ToString("N");

  await ThrowsAsync<InvalidOperationException>(() => repository.CommitCaptureAsync(
      CreateCapture(key, "不得半提交") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
        {
          new CapturedPostImage(1, "https://img.test/atomic.jpg")
        })
      },
      CreateSnapshot(missingRunId, key, mediaPending: true)));

  TestAssert.Equal<StoredPost?>(null, await repository.GetAsync(key, includeDeleted: true));
  TestAssert.Equal<StoredCaptureSnapshot?>(
      null,
      await repository.GetCaptureSnapshotAsync(missingRunId, key.ToString()));

  var runId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, runId, key);
  var wrongPost = new PostKey("discuz.example", "wrong-work-binding");
  await ThrowsAsync<ArgumentException>(() => repository.CommitCaptureAsync(
      CreateCapture(wrongPost, "不得绑定到别的 work"),
      CreateSnapshot(runId, wrongPost, mediaPending: false) with
      {
        WorkItemKey = key.ToString()
      }));
  TestAssert.Equal<StoredPost?>(null, await repository.GetAsync(wrongPost, includeDeleted: true));

  var metadata = CreateSnapshot(runId, key, mediaPending: true);
  await repository.CommitCaptureAsync(CreateCapture(key, "原子捕获成功"), metadata);

  TestAssert.NotNull(await repository.GetAsync(key));
  var snapshot = TestAssert.NotNull(await repository.GetCaptureSnapshotAsync(runId, key.ToString()));
  TestAssert.Equal(key, snapshot.PostKey);
  TestAssert.Equal(metadata.RequestedUrl, snapshot.RequestedUrl);
  TestAssert.Equal(metadata.FinalUrl, snapshot.FinalUrl);
  TestAssert.Equal("thread-html", snapshot.Kind);
  TestAssert.Equal(new string('a', 64), snapshot.Sha256);
  TestAssert.Equal(4096L, snapshot.ByteCount);

  await using var verify = database.CreateConnection();
  await verify.OpenAsync();
  TestAssert.Equal(
      "captured",
      await ScalarStringAsync(
          verify,
          $"SELECT state FROM collection_work_items WHERE run_id = '{runId}';"));
  TestAssert.Equal(
      1L,
      await ScalarInt64Async(
          verify,
          $"SELECT media_pending FROM collection_work_items WHERE run_id = '{runId}';"));
  TestAssert.Equal(
      "{\"remaining\":1}",
      await ScalarStringAsync(
          verify,
          $"SELECT work_parameters_json FROM collection_work_items WHERE run_id = '{runId}';"));
  TestAssert.Equal(
      0L,
      await ScalarInt64Async(
          verify,
          $"SELECT threads_committed FROM collection_runs WHERE id = '{runId}';"));
}

static async Task FinalizeCaptureIsAtomicAndIdempotent()
{
  var database = new BookmarkDatabase(CreateDatabasePath());
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var key = new PostKey("discuz.example", "finalize-once");
  var runId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, runId, key);
  await repository.CommitCaptureAsync(
      CreateCapture(key, "等待媒体"),
      CreateSnapshot(runId, key, mediaPending: true));

  TestAssert.Equal(true, await repository.FinalizeCaptureAsync(runId, key.ToString()));
  TestAssert.Equal(false, await repository.FinalizeCaptureAsync(runId, key.ToString()));

  var snapshot = TestAssert.NotNull(await repository.GetCaptureSnapshotAsync(runId, key.ToString()));
  TestAssert.Equal("committed", snapshot.WorkState);
  TestAssert.Equal(false, snapshot.MediaPending);
  await using var verify = database.CreateConnection();
  await verify.OpenAsync();
  TestAssert.Equal(
      1L,
      await ScalarInt64Async(
          verify,
          $"SELECT threads_committed FROM collection_runs WHERE id = '{runId}';"));
  TestAssert.Equal(
      1L,
      await ScalarInt64Async(
          verify,
          $"SELECT count(*) FROM collection_work_items WHERE run_id = '{runId}' AND committed_at IS NOT NULL;"));
  await verify.DisposeAsync();

  TestAssert.Equal(1, await repository.MoveToTrashAsync(new[] { key }));
  TestAssert.Equal(1, (await repository.PurgeAsync(new[] { key })).PurgedCount);
  TestAssert.Equal<StoredCaptureSnapshot?>(
      null,
      await repository.GetCaptureSnapshotAsync(runId, key.ToString()));
}

static async Task BoundFinalizeRejectsPendingOrMissingExpectedImages()
{
  var database = new BookmarkDatabase(CreateDatabasePath());
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var key = new PostKey("discuz.example", "finalize-manifest");
  var runId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, runId, key);
  await repository.CommitCaptureAsync(
      CreateCapture(key, "等待完整媒体") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
        {
          new CapturedPostImage(1, "https://img.test/finalize-one.jpg"),
          new CapturedPostImage(2, "https://img.test/finalize-two.jpg")
        })
      },
      CreateSnapshot(runId, key, mediaPending: true));
  var snapshot = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(runId, key.ToString()));
  var request = CreateFinalizeRequest(snapshot);

  TestAssert.Equal(true, await repository.UpdateImageDownloadAsync(
      new ImageDownloadUpdateRequest
      {
        PostKey = key,
        SnapshotId = snapshot.SnapshotId,
        CaptureGeneration = snapshot.CaptureGeneration,
        SourceUrl = "https://img.test/finalize-one.jpg",
        LocalPath = "images/finalize-one.jpg",
        DownloadStatus = "downloaded"
      }));
  await ThrowsAsync<InvalidOperationException>(() => repository.FinalizeCaptureAsync(request));

  await using (var mutate = database.CreateConnection())
  {
    await mutate.OpenAsync();
    await ExecuteAsync(
        mutate,
        $"DELETE FROM post_images WHERE post_id = {snapshot.PostRowId} " +
        $"AND capture_generation = {snapshot.CaptureGeneration} AND position = 2;");
  }

  await ThrowsAsync<InvalidOperationException>(() => repository.FinalizeCaptureAsync(request));
  await using var verify = database.CreateConnection();
  await verify.OpenAsync();
  TestAssert.Equal(
      "captured",
      await ScalarStringAsync(
          verify,
          $"SELECT state FROM collection_work_items WHERE run_id = '{runId}';"));
  TestAssert.Equal(
      0L,
      await ScalarInt64Async(
          verify,
          $"SELECT threads_committed FROM collection_runs WHERE id = '{runId}';"));
}

static async Task BoundFinalizeRejectsStaleOrMismatchedCaptureAndCountsOnce()
{
  var database = new BookmarkDatabase(CreateDatabasePath());
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var key = new PostKey("discuz.example", "finalize-bound");
  var runId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, runId, key);

  await repository.CommitCaptureAsync(
      CreateCapture(key, "第一代") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
        {
          new CapturedPostImage(
              1,
              "https://img.test/first-generation.jpg",
              LocalPath: "images/first-generation.jpg",
              DownloadStatus: "downloaded")
        })
      },
      CreateSnapshot(runId, key, mediaPending: false));
  var firstSnapshot = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(runId, key.ToString()));

  await repository.CommitCaptureAsync(
      CreateCapture(key, "第二代") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
        {
          new CapturedPostImage(
              1,
              "https://img.test/second-generation.jpg",
              DownloadStatus: "failed")
        })
      },
      CreateSnapshot(runId, key, mediaPending: true) with
      {
        Sha256 = new string('b', 64),
        CapturedAt = new DateTimeOffset(2026, 8, 24, 12, 1, 0, TimeSpan.Zero)
      });
  var secondSnapshot = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(runId, key.ToString()));
  var current = CreateFinalizeRequest(secondSnapshot);

  await ThrowsAsync<InvalidOperationException>(() =>
      repository.FinalizeCaptureAsync(CreateFinalizeRequest(firstSnapshot)));
  await ThrowsAsync<InvalidOperationException>(() =>
      repository.FinalizeCaptureAsync(current with
      {
        PostKey = new PostKey(key.SiteFamily, "different-post")
      }));
  await ThrowsAsync<InvalidOperationException>(() =>
      repository.FinalizeCaptureAsync(current with { SnapshotId = current.SnapshotId + 1 }));
  await ThrowsAsync<InvalidOperationException>(() =>
      repository.FinalizeCaptureAsync(current with
      {
        CaptureGeneration = current.CaptureGeneration + 1
      }));

  TestAssert.Equal(true, await repository.FinalizeCaptureAsync(current));
  TestAssert.Equal(false, await repository.FinalizeCaptureAsync(current));
  var finalized = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(runId, key.ToString()));
  TestAssert.Equal("committed", finalized.WorkState);
  TestAssert.Equal(false, finalized.MediaPending);

  await using var verify = database.CreateConnection();
  await verify.OpenAsync();
  TestAssert.Equal(
      1L,
      await ScalarInt64Async(
          verify,
          $"SELECT threads_committed FROM collection_runs WHERE id = '{runId}';"));
}

static async Task BoundImageUpdateRejectsStaleWorkerForSameSourceUrl()
{
  var database = new BookmarkDatabase(CreateDatabasePath());
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var key = new PostKey("discuz.example", "stale-image-worker");
  const string sourceUrl = "https://img.test/same-source.jpg";
  var runId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, runId, key);

  var capture = CreateCapture(key, "同源第一代") with
  {
    Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
    {
      new CapturedPostImage(1, sourceUrl)
    })
  };
  await repository.CommitCaptureAsync(capture, CreateSnapshot(runId, key, mediaPending: true));
  var first = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(runId, key.ToString()));

  await repository.CommitCaptureAsync(
      capture with { Title = Observation<string>.Observed("同源第二代") },
      CreateSnapshot(runId, key, mediaPending: true) with
      {
        Sha256 = new string('c', 64),
        CapturedAt = new DateTimeOffset(2026, 8, 24, 12, 2, 0, TimeSpan.Zero)
      });
  var second = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(runId, key.ToString()));

  await ThrowsAsync<InvalidOperationException>(() => repository.UpdateImageDownloadAsync(
      new ImageDownloadUpdateRequest
      {
        PostKey = key,
        SnapshotId = first.SnapshotId,
        CaptureGeneration = first.CaptureGeneration,
        SourceUrl = sourceUrl,
        LocalPath = "images/stale-worker.jpg",
        DownloadStatus = "downloaded"
      }));

  TestAssert.Equal(true, await repository.UpdateImageDownloadAsync(
      new ImageDownloadUpdateRequest
      {
        PostKey = key,
        SnapshotId = second.SnapshotId,
        CaptureGeneration = second.CaptureGeneration,
        SourceUrl = sourceUrl,
        LocalPath = "images/current-worker.jpg",
        DownloadStatus = "downloaded"
      }));

  await using var verify = database.CreateConnection();
  await verify.OpenAsync();
  TestAssert.Equal(
      "pending",
      await ScalarStringAsync(
          verify,
          $"SELECT download_status FROM post_images WHERE capture_generation = {first.CaptureGeneration};"));
  TestAssert.Equal(
      "images/current-worker.jpg",
      await ScalarStringAsync(
          verify,
          $"SELECT local_path FROM post_images WHERE capture_generation = {second.CaptureGeneration};"));
}

static async Task FailedMediaRefreshPreservesDownloadedFile()
{
  var repository = await CreateRepositoryAsync();
  var key = new PostKey("discuz.example", "preserve-good-media");
  const string sourceUrl = "https://img.test/preserve-good.jpg";
  await repository.UpsertCaptureAsync(CreateCapture(key, "已有成功图片") with
  {
    Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
    {
      new CapturedPostImage(
          1,
          sourceUrl,
          LocalPath: "images/good.jpg",
          ThumbnailPath: "images/good.thumb.jpg",
          DownloadStatus: "downloaded")
    })
  });

  await repository.UpsertCaptureAsync(CreateCapture(key, "重采未完成") with
  {
    Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
    {
      new CapturedPostImage(1, sourceUrl, LocalPath: "", ThumbnailPath: "", DownloadStatus: "failed")
    })
  });
  var afterRecrawl = TestAssert.NotNull(await repository.GetAsync(key)).Images[0];
  TestAssert.Equal("images/good.jpg", afterRecrawl.LocalPath);
  TestAssert.Equal("images/good.thumb.jpg", afterRecrawl.ThumbnailPath);
  TestAssert.Equal("downloaded", afterRecrawl.DownloadStatus);

  TestAssert.Equal(true, await repository.UpdateImageDownloadAsync(
      key, sourceUrl, localPath: null, thumbnailPath: null, downloadStatus: "failed"));
  var afterFailure = TestAssert.NotNull(await repository.GetAsync(key)).Images[0];
  TestAssert.Equal("images/good.jpg", afterFailure.LocalPath);
  TestAssert.Equal("downloaded", afterFailure.DownloadStatus);

  TestAssert.Equal(true, await repository.UpdateImageDownloadAsync(
      key, sourceUrl, localPath: "", thumbnailPath: null, downloadStatus: "downloaded"));
  var afterEmptySuccess = TestAssert.NotNull(await repository.GetAsync(key)).Images[0];
  TestAssert.Equal("images/good.jpg", afterEmptySuccess.LocalPath);
  TestAssert.Equal("downloaded", afterEmptySuccess.DownloadStatus);

  TestAssert.Equal(true, await repository.UpdateImageDownloadAsync(
      key,
      sourceUrl,
      localPath: "images/replacement.jpg",
      thumbnailPath: "images/replacement.thumb.jpg",
      downloadStatus: "downloaded"));
  var afterSuccess = TestAssert.NotNull(await repository.GetAsync(key)).Images[0];
  TestAssert.Equal("images/replacement.jpg", afterSuccess.LocalPath);
  TestAssert.Equal("images/replacement.thumb.jpg", afterSuccess.ThumbnailPath);
}

static async Task ChangedImageSourceStagesNewGenerationWithoutDroppingFallback()
{
  var database = new BookmarkDatabase(CreateDatabasePath());
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var key = new PostKey("discuz.example", "changed-image-generation");

  var firstRunId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, firstRunId, key);
  await repository.CommitCaptureAsync(
      CreateCapture(key, "旧快照") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
        {
          new CapturedPostImage(
              1,
              "https://img.test/old-success.jpg",
              LocalPath: "images/old-success.jpg",
              DownloadStatus: "downloaded")
        })
      },
      CreateSnapshot(firstRunId, key, mediaPending: false));
  var firstSnapshot = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(firstRunId, key.ToString()));

  await using (var connection = database.CreateConnection())
  {
    await connection.OpenAsync();
    await ExecuteAsync(
        connection,
        $"UPDATE collection_runs SET state = 'Succeeded' WHERE id = '{firstRunId}';");
  }

  var secondRunId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, secondRunId, key);
  await repository.CommitCaptureAsync(
      CreateCapture(key, "新快照") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
        {
          new CapturedPostImage(1, "https://img.test/new-pending.jpg")
        })
      },
      CreateSnapshot(secondRunId, key, mediaPending: true) with
      {
        Sha256 = new string('b', 64)
      });

  var secondSnapshot = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(secondRunId, key.ToString()));
  TestAssert.Equal(true, secondSnapshot.CaptureGeneration > firstSnapshot.CaptureGeneration);

  var images = TestAssert.NotNull(await repository.GetAsync(key)).Images;
  TestAssert.Equal(1, images.Count);
  var fallback = images.Single();
  TestAssert.Equal("https://img.test/old-success.jpg", fallback.SourceUrl);
  TestAssert.Equal(firstSnapshot.CaptureGeneration, fallback.CaptureGeneration);
  TestAssert.Equal(true, fallback.IsFallback);
  TestAssert.Equal("images/old-success.jpg", fallback.LocalPath);
  TestAssert.Equal("downloaded", fallback.DownloadStatus);

  var page = await repository.QueryAsync(new PostQuery { Search = "新快照" });
  TestAssert.Equal(1, page.Items.Count);
  TestAssert.Equal(1, page.Items[0].Images.Count);
  TestAssert.Equal("https://img.test/old-success.jpg", page.Items[0].Images[0].SourceUrl);
  TestAssert.Equal(true, page.Items[0].Images[0].IsFallback);

  TestAssert.Equal(true, await repository.UpdateImageDownloadAsync(
      new ImageDownloadUpdateRequest
      {
        PostKey = key,
        SnapshotId = secondSnapshot.SnapshotId,
        CaptureGeneration = secondSnapshot.CaptureGeneration,
        SourceUrl = "https://img.test/new-pending.jpg",
        LocalPath = "images/new-success.jpg",
        DownloadStatus = "downloaded"
      }));
  var replaced = TestAssert.NotNull(await repository.GetAsync(key)).Images;
  TestAssert.Equal(1, replaced.Count);
  TestAssert.Equal("https://img.test/new-pending.jpg", replaced[0].SourceUrl);
  TestAssert.Equal(secondSnapshot.CaptureGeneration, replaced[0].CaptureGeneration);
  TestAssert.Equal(false, replaced[0].IsFallback);
  TestAssert.Equal("images/new-success.jpg", replaced[0].LocalPath);
}

static async Task UnobservedImagesCarryForwardCurrentManifest()
{
  var database = new BookmarkDatabase(CreateDatabasePath());
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var key = new PostKey("discuz.example", "unobserved-images");

  var firstRunId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, firstRunId, key);
  await repository.CommitCaptureAsync(
      CreateCapture(key, "首个快照") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
        {
          new CapturedPostImage(
              1,
              "https://img.test/preserved.jpg",
              LocalPath: "images/preserved.jpg",
              DownloadStatus: "downloaded")
        })
      },
      CreateSnapshot(firstRunId, key, mediaPending: false));
  var firstSnapshot = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(firstRunId, key.ToString()));
  TestAssert.Equal(true, await repository.FinalizeCaptureAsync(CreateFinalizeRequest(firstSnapshot)));
  await using (var connection = database.CreateConnection())
  {
    await connection.OpenAsync();
    await ExecuteAsync(
        connection,
        $"UPDATE collection_runs SET state = 'Succeeded' WHERE id = '{firstRunId}';");
  }

  var secondRunId = Guid.NewGuid().ToString("N");
  await CreateRunAndWorkItemAsync(database, secondRunId, key);
  await repository.CommitCaptureAsync(
      CreateCapture(key, "不可观察图片快照") with
      {
        Images = Observation<IReadOnlyList<CapturedPostImage>>.Unobserved
      },
      CreateSnapshot(secondRunId, key, mediaPending: false) with
      {
        Sha256 = new string('c', 64)
      });

  var secondSnapshot = TestAssert.NotNull(
      await repository.GetCaptureSnapshotAsync(secondRunId, key.ToString()));
  TestAssert.Equal(true, secondSnapshot.CaptureGeneration > firstSnapshot.CaptureGeneration);
  TestAssert.Equal(1, secondSnapshot.ExpectedImageCount);
  var saved = TestAssert.NotNull(await repository.GetAsync(key));
  TestAssert.Equal(1, saved.Images.Count);
  TestAssert.Equal("https://img.test/preserved.jpg", saved.Images[0].SourceUrl);
  TestAssert.Equal("images/preserved.jpg", saved.Images[0].LocalPath);
  TestAssert.Equal(secondSnapshot.CaptureGeneration, saved.Images[0].CaptureGeneration);
  TestAssert.Equal(false, saved.Images[0].IsFallback);
  TestAssert.Equal(true, await repository.FinalizeCaptureAsync(CreateFinalizeRequest(secondSnapshot)));
}

static async Task ImageDownloadResultCanBeCommittedIndependently()
{
  var repository = await CreateRepositoryAsync();
  var key = new PostKey("discuz.example", "image-worker");
  await repository.UpsertCaptureAsync(CreateCapture(key, "异步图片") with
  {
    Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(new[]
    {
      new CapturedPostImage(1, "https://img.test/worker.jpg")
    })
  });

  TestAssert.Equal(true, await repository.UpdateImageDownloadAsync(
    key,
    "https://img.test/worker.jpg",
    "images/worker.jpg",
    "images/worker.thumb.jpg",
    "downloaded"));

  var saved = TestAssert.NotNull(await repository.GetAsync(key));
  TestAssert.Equal("images/worker.jpg", saved.Images[0].LocalPath);
  TestAssert.Equal("downloaded", saved.Images[0].DownloadStatus);
  TestAssert.Equal(false, await repository.UpdateImageDownloadAsync(
    key,
    "https://img.test/missing.jpg",
    null,
    null,
      "failed"));
}

static CaptureSnapshotMetadata CreateSnapshot(string runId, PostKey key, bool mediaPending)
{
  return new CaptureSnapshotMetadata
  {
    RunId = runId,
    WorkItemKey = key.ToString(),
    PostKey = key,
    RequestedUrl = $"https://example.test/requested-{key.ThreadId}",
    FinalUrl = $"https://example.test/thread-{key.ThreadId}-1-1.html",
    Kind = "thread-html",
    Sha256 = new string('a', 64),
    CapturedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
    ByteCount = 4096,
    MediaPending = mediaPending,
    WorkParametersJson = mediaPending ? "{\"remaining\":1}" : null
  };
}

static CaptureFinalizeRequest CreateFinalizeRequest(StoredCaptureSnapshot snapshot) => new()
{
  RunId = snapshot.RunId,
  WorkItemKey = snapshot.WorkItemKey,
  PostKey = snapshot.PostKey,
  SnapshotId = snapshot.SnapshotId,
  CaptureGeneration = snapshot.CaptureGeneration
};

static async Task CreateRunAndWorkItemAsync(
    BookmarkDatabase database,
    string runId,
    PostKey key)
{
  await using var connection = database.CreateConnection();
  await connection.OpenAsync();
  await using var command = connection.CreateCommand();
  command.CommandText = """
    INSERT INTO collection_runs (id, state, plan_json, started_at)
    VALUES ($run_id, 'Collecting', 'plan', '2026-08-24T00:00:00Z');
    INSERT INTO collection_work_items (run_id, logical_key, uri, state)
    VALUES ($run_id, $logical_key, $uri, 'processing');
    """;
  command.Parameters.AddWithValue("$run_id", runId);
  command.Parameters.AddWithValue("$logical_key", key.ToString());
  command.Parameters.AddWithValue("$uri", $"https://example.test/thread-{key.ThreadId}-1-1.html");
  await command.ExecuteNonQueryAsync();
}

static PostCapture CreateCapture(
    PostKey key,
    string title,
    Observation<string?> body = default,
    Observation<IReadOnlyList<CapturedPostImage>> images = default,
    Observation<IReadOnlyList<CapturedResourceLink>> links = default)
{
  return new PostCapture
  {
    Key = key,
    CanonicalUrl = $"https://example.test/thread-{key.ThreadId}-1-1.html",
    Title = Observation<string>.Observed(title),
    ProjectType = Observation<string>.Observed("未分类"),
    BodyText = body,
    Images = images,
    ResourceLinks = links
  };
}

static async Task<PostRepository> CreateRepositoryAsync()
{
  var path = CreateDatabasePath();

  var database = new BookmarkDatabase(path);
  await database.InitializeAsync();
  return new PostRepository(database);
}

static string CreateDatabasePath()
{
  var path = Path.Combine(Path.GetTempPath(), "rfbm-tests", $"{Guid.NewGuid():N}.sqlite3");
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  return path;
}

static async Task<long> ScalarInt64Async(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
{
  await using var command = connection.CreateCommand();
  command.CommandText = sql;
  return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<string> ScalarStringAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
{
  await using var command = connection.CreateCommand();
  command.CommandText = sql;
  return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
}

static async Task ExecuteAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
{
  await using var command = connection.CreateCommand();
  command.CommandText = sql;
  await command.ExecuteNonQueryAsync();
}

static async Task ThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
  try
  {
    await action();
  }
  catch (TException)
  {
    return;
  }

  throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
}
