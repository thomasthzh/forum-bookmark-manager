using Microsoft.Data.Sqlite;

namespace Rfbm.Data.Storage;

public sealed class BookmarkDatabase
{
  public const int CurrentSchemaVersion = 3;

  public BookmarkDatabase(string databasePath)
  {
    if (string.IsNullOrWhiteSpace(databasePath))
    {
      throw new ArgumentException("Database path is required.", nameof(databasePath));
    }

    DatabasePath = Path.GetFullPath(databasePath);
  }

  public string DatabasePath { get; }

  public async Task InitializeAsync(CancellationToken cancellationToken = default)
  {
    var directory = Path.GetDirectoryName(DatabasePath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
      Directory.CreateDirectory(directory);
    }

    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await ConfigureConnectionAsync(connection, cancellationToken);

    var version = await ReadUserVersionAsync(connection, cancellationToken);
    if (version > CurrentSchemaVersion)
    {
      throw new InvalidOperationException(
          $"Database schema version {version} is newer than supported version {CurrentSchemaVersion}.");
    }

    if (version == 0)
    {
      var postColumns = await ReadTableColumnsAsync(connection, "posts", cancellationToken);
      if (postColumns.Count == 0)
      {
        await CreateVersionOneSchemaAsync(connection, cancellationToken);
      }
      else if (postColumns.Contains("id") && postColumns.Contains("url") && postColumns.Contains("type"))
      {
        await MigrateProbeSchemaAsync(connection, cancellationToken);
      }
      else if (postColumns.Contains("post_url"))
      {
        throw new InvalidOperationException(
            "This is a legacy Python database. Import it into a separate native database instead of opening it in place.");
      }
      else
      {
        throw new InvalidOperationException("Unrecognized version 0 database schema.");
      }
    }
    else if (version == 1)
    {
      await MigrateVersionOneAsync(connection, cancellationToken);
    }
    else if (version == 2)
    {
      await MigrateVersionTwoAsync(connection, cancellationToken);
    }
  }

  public SqliteConnection CreateConnection()
  {
    var builder = new SqliteConnectionStringBuilder
    {
      DataSource = DatabasePath,
      Mode = SqliteOpenMode.ReadWriteCreate,
      Cache = SqliteCacheMode.Shared,
      ForeignKeys = true,
      DefaultTimeout = 5
    };
    return new SqliteConnection(builder.ToString());
  }

  internal static async Task ConfigureConnectionAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken = default)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<int> ReadUserVersionAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
  }

  private static async Task CreateVersionOneSchemaAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var transaction = connection.BeginTransaction(deferred: false);
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = VersionOneSchema + VersionTwoSchema + VersionThreeSchema;
    await command.ExecuteNonQueryAsync(cancellationToken);

    await using var version = connection.CreateCommand();
    version.Transaction = transaction;
    version.CommandText = "PRAGMA user_version = 3;";
    await version.ExecuteNonQueryAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    await EnableWalAsync(connection, cancellationToken);
  }

  private static async Task MigrateProbeSchemaAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var transaction = connection.BeginTransaction(deferred: false);
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "ALTER TABLE posts RENAME TO posts_probe_v0;" + VersionOneSchema + """

            INSERT INTO posts (
                site_family, thread_id, canonical_url, raw_url, title, project_type, status,
                favorite_time_raw, favorite_time_utc, created_at, updated_at, last_crawled_at)
            SELECT
                'legacy', id, url, url, title, type,
                CASE status
                    WHEN 'downloaded' THEN 'downloaded'
                    WHEN 'link_invalid' THEN 'link_invalid'
                    WHEN '已下载' THEN 'downloaded'
                    WHEN '链接失效' THEN 'link_invalid'
                    ELSE 'not_downloaded'
                END,
                favorite_time, favorite_time, created_at, updated_at, updated_at
            FROM posts_probe_v0;

            DROP TABLE posts_probe_v0;
            """ + VersionTwoSchema + VersionThreeSchema;
    await command.ExecuteNonQueryAsync(cancellationToken);

    await using var version = connection.CreateCommand();
    version.Transaction = transaction;
    version.CommandText = "PRAGMA user_version = 3;";
    await version.ExecuteNonQueryAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    await EnableWalAsync(connection, cancellationToken);
  }

  private static async Task MigrateVersionOneAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var transaction = connection.BeginTransaction(deferred: false);
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = VersionTwoSchema + VersionThreeSchema;
    await command.ExecuteNonQueryAsync(cancellationToken);

    await using var version = connection.CreateCommand();
    version.Transaction = transaction;
    version.CommandText = "PRAGMA user_version = 3;";
    await version.ExecuteNonQueryAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    await EnableWalAsync(connection, cancellationToken);
  }

  private static async Task MigrateVersionTwoAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var transaction = connection.BeginTransaction(deferred: false);
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = VersionThreeSchema;
    await command.ExecuteNonQueryAsync(cancellationToken);

    await using var version = connection.CreateCommand();
    version.Transaction = transaction;
    version.CommandText = "PRAGMA user_version = 3;";
    await version.ExecuteNonQueryAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    await EnableWalAsync(connection, cancellationToken);
  }

  private static async Task EnableWalAsync(
      SqliteConnection connection,
      CancellationToken cancellationToken)
  {
    await using var wal = connection.CreateCommand();
    wal.CommandText = "PRAGMA journal_mode = WAL;";
    await wal.ExecuteScalarAsync(cancellationToken);
  }

  private static async Task<HashSet<string>> ReadTableColumnsAsync(
      SqliteConnection connection,
      string table,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({table});";
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      columns.Add(reader.GetString(1));
    }

    return columns;
  }

  private const string VersionOneSchema = """
        CREATE TABLE schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            applied_at TEXT NOT NULL
        );

        INSERT INTO schema_migrations (version, name, applied_at)
        VALUES (1, 'native_baseline', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

        CREATE TABLE posts (
            row_id INTEGER PRIMARY KEY AUTOINCREMENT,
            site_family TEXT NOT NULL,
            thread_id TEXT NOT NULL,
            canonical_url TEXT NOT NULL,
            raw_url TEXT,
            mirror_url TEXT,
            favorite_url TEXT,
            title TEXT NOT NULL,
            project_type TEXT NOT NULL DEFAULT '未分类',
            status TEXT NOT NULL DEFAULT 'not_downloaded'
                CHECK (status IN ('not_downloaded', 'downloaded', 'link_invalid')),
            favorite_time_raw TEXT,
            favorite_time_utc TEXT,
            download_count INTEGER,
            visit_count INTEGER,
            favorite_count INTEGER,
            extract_password TEXT,
            body_text TEXT,
            is_starred INTEGER NOT NULL DEFAULT 0 CHECK (is_starred IN (0, 1)),
            note TEXT NOT NULL DEFAULT '',
            deleted_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            last_crawled_at TEXT NOT NULL
        );

        CREATE UNIQUE INDEX ux_posts_identity ON posts(site_family, thread_id);
        CREATE INDEX ix_posts_favorite_time ON posts(favorite_time_utc DESC, row_id DESC);
        CREATE INDEX ix_posts_status_type ON posts(status, project_type);
        CREATE INDEX ix_posts_deleted ON posts(deleted_at);

        CREATE TABLE post_images (
            image_id INTEGER PRIMARY KEY AUTOINCREMENT,
            post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
            position INTEGER NOT NULL CHECK (position > 0),
            source_url TEXT NOT NULL,
            local_path TEXT,
            thumbnail_path TEXT,
            download_status TEXT NOT NULL DEFAULT 'pending',
            UNIQUE(post_id, position),
            UNIQUE(post_id, source_url)
        );

        CREATE TABLE resource_links (
            link_id INTEGER PRIMARY KEY AUTOINCREMENT,
            post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
            position INTEGER NOT NULL CHECK (position > 0),
            url TEXT NOT NULL,
            label TEXT NOT NULL DEFAULT '',
            context_text TEXT NOT NULL DEFAULT '',
            UNIQUE(post_id, position)
        );

        CREATE TABLE tags (
            tag_id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );

        CREATE TABLE post_tags (
            post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
            PRIMARY KEY(post_id, tag_id)
        );

        CREATE TABLE collection_runs (
            id TEXT PRIMARY KEY,
            state TEXT NOT NULL,
            plan_json TEXT NOT NULL,
            started_at TEXT NOT NULL,
            finished_at TEXT NULL,
            pages_discovered INTEGER NOT NULL DEFAULT 0,
            pages_committed INTEGER NOT NULL DEFAULT 0,
            pages_failed INTEGER NOT NULL DEFAULT 0,
            threads_discovered INTEGER NOT NULL DEFAULT 0,
            threads_committed INTEGER NOT NULL DEFAULT 0,
            threads_failed INTEGER NOT NULL DEFAULT 0,
            can_resume INTEGER NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX ux_collection_runs_one_active
        ON collection_runs((1))
        WHERE state IN ('Preparing', 'WaitingForUser', 'Discovering', 'Collecting', 'CancelRequested');

        CREATE TABLE collection_frontier (
            run_id TEXT NOT NULL,
            logical_key TEXT NOT NULL,
            uri TEXT NOT NULL,
            state TEXT NOT NULL,
            PRIMARY KEY (run_id, logical_key),
            FOREIGN KEY (run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
        );

        CREATE TABLE collection_work_items (
            run_id TEXT NOT NULL,
            logical_key TEXT NOT NULL,
            uri TEXT NOT NULL,
            state TEXT NOT NULL,
            PRIMARY KEY (run_id, logical_key),
            FOREIGN KEY (run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
        );

        CREATE TABLE collection_events (
            event_id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES collection_runs(id) ON DELETE CASCADE,
            sequence INTEGER NOT NULL,
            level TEXT NOT NULL,
            event_type TEXT NOT NULL,
            message TEXT NOT NULL,
            url TEXT,
            created_at TEXT NOT NULL,
            UNIQUE(run_id, sequence)
        );
        """;

  private const string VersionTwoSchema = """
        CREATE TABLE capture_snapshots (
            snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            work_item_key TEXT NOT NULL,
            post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
            requested_url TEXT NOT NULL,
            final_url TEXT NOT NULL,
            kind TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            byte_count INTEGER NOT NULL CHECK (byte_count >= 0),
            UNIQUE(run_id, work_item_key, sha256),
            FOREIGN KEY (run_id, work_item_key)
                REFERENCES collection_work_items(run_id, logical_key) ON DELETE CASCADE
        );

        CREATE INDEX ix_capture_snapshots_post
        ON capture_snapshots(post_id, captured_at DESC);

        ALTER TABLE collection_work_items
        ADD COLUMN post_id INTEGER REFERENCES posts(row_id) ON DELETE SET NULL;

        ALTER TABLE collection_work_items
        ADD COLUMN snapshot_id INTEGER REFERENCES capture_snapshots(snapshot_id) ON DELETE SET NULL;

        ALTER TABLE collection_work_items
        ADD COLUMN media_pending INTEGER NOT NULL DEFAULT 0 CHECK (media_pending IN (0, 1));

        ALTER TABLE collection_work_items
        ADD COLUMN work_parameters_json TEXT;

        ALTER TABLE collection_work_items
        ADD COLUMN captured_at TEXT;

        ALTER TABLE collection_work_items
        ADD COLUMN committed_at TEXT;

        INSERT INTO schema_migrations (version, name, applied_at)
        VALUES (2, 'durable_capture_checkpoint', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """;

  private const string VersionThreeSchema = """
        ALTER TABLE capture_snapshots
        ADD COLUMN capture_generation INTEGER NOT NULL DEFAULT 0
            CHECK (capture_generation >= 0);

        ALTER TABLE capture_snapshots
        ADD COLUMN expected_image_count INTEGER NOT NULL DEFAULT 0
            CHECK (expected_image_count >= 0);

        ALTER TABLE collection_work_items
        ADD COLUMN capture_generation INTEGER NOT NULL DEFAULT 0
            CHECK (capture_generation >= 0);

        CREATE TABLE capture_expected_images (
            snapshot_id INTEGER NOT NULL
                REFERENCES capture_snapshots(snapshot_id) ON DELETE CASCADE,
            position INTEGER NOT NULL CHECK (position > 0),
            source_url TEXT NOT NULL,
            PRIMARY KEY (snapshot_id, position),
            UNIQUE (snapshot_id, source_url)
        );

        ALTER TABLE post_images RENAME TO post_images_v2;

        CREATE TABLE post_images (
            image_id INTEGER PRIMARY KEY AUTOINCREMENT,
            post_id INTEGER NOT NULL REFERENCES posts(row_id) ON DELETE CASCADE,
            capture_generation INTEGER NOT NULL DEFAULT 0 CHECK (capture_generation >= 0),
            position INTEGER NOT NULL CHECK (position > 0),
            source_url TEXT NOT NULL,
            local_path TEXT,
            thumbnail_path TEXT,
            download_status TEXT NOT NULL DEFAULT 'pending',
            UNIQUE(post_id, capture_generation, position),
            UNIQUE(post_id, capture_generation, source_url)
        );

        INSERT INTO post_images (
            image_id, post_id, capture_generation, position, source_url,
            local_path, thumbnail_path, download_status)
        SELECT image_id, post_id, 0, position, source_url,
               local_path, thumbnail_path, download_status
        FROM post_images_v2;

        DROP TABLE post_images_v2;

        CREATE INDEX ix_post_images_generation
        ON post_images(post_id, capture_generation DESC, position);

        UPDATE capture_snapshots
        SET expected_image_count = (
            SELECT count(*) FROM post_images i
            WHERE i.post_id = capture_snapshots.post_id
              AND i.capture_generation = capture_snapshots.capture_generation);

        INSERT INTO capture_expected_images (snapshot_id, position, source_url)
        SELECT s.snapshot_id, i.position, i.source_url
        FROM capture_snapshots s
        JOIN post_images i
          ON i.post_id = s.post_id
         AND i.capture_generation = s.capture_generation;

        UPDATE collection_work_items
        SET capture_generation = COALESCE((
            SELECT s.capture_generation FROM capture_snapshots s
            WHERE s.snapshot_id = collection_work_items.snapshot_id), 0);

        INSERT INTO schema_migrations (version, name, applied_at)
        VALUES (3, 'capture_generation_staging', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """;
}
