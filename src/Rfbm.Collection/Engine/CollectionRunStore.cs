using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rfbm.Collection.Contracts;
using Rfbm.Data.Storage;

namespace Rfbm.Collection.Engine;

internal sealed class CollectionRunStore
{
  private readonly string _databasePath;
  private readonly BookmarkDatabase _database;

  public CollectionRunStore(string databasePath)
  {
    _databasePath = databasePath;
    _database = new BookmarkDatabase(databasePath);
  }

  public async Task InitializeAsync(CancellationToken cancellationToken)
  {
    await _database.InitializeAsync(cancellationToken);
  }

  public async Task RecoverAbandonedRunsAsync(CancellationToken cancellationToken)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            UPDATE collection_runs
            SET state = 'Interrupted',
                finished_at = $finished_at,
                can_resume = 1
            WHERE state IN ('Preparing', 'WaitingForUser', 'Discovering', 'Collecting', 'CancelRequested');
            """;
    command.Parameters.AddWithValue("$finished_at", DateTimeOffset.UtcNow.ToString("O"));
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  public async Task CreateAsync(CollectionRunId id, CollectionPlan plan, CancellationToken cancellationToken)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            INSERT INTO collection_runs (id, state, plan_json, started_at)
            VALUES ($id, 'Preparing', $plan, $started);
            """;
    command.Parameters.AddWithValue("$id", id.ToString());
    command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(plan));
    command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));

    try
    {
      await command.ExecuteNonQueryAsync(cancellationToken);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
      throw new CollectionOpenException(CollectionErrorCode.EngineBusy, "已有采集任务正在运行。", ex);
    }
  }

  public async Task<CollectionPlan> ReactivateAsync(
      CollectionRunId id,
      CancellationToken cancellationToken)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

    string? planJson;
    await using (var read = connection.CreateCommand())
    {
      read.Transaction = (SqliteTransaction)transaction;
      read.CommandText = """
                SELECT plan_json
                FROM collection_runs
                WHERE id = $id AND can_resume = 1;
                """;
      read.Parameters.AddWithValue("$id", id.ToString());
      planJson = (string?)await read.ExecuteScalarAsync(cancellationToken);
    }

    if (planJson is null)
    {
      throw new CollectionOpenException(
          CollectionErrorCode.RunNotRecoverable,
          "找不到可恢复的采集任务。");
    }

    await using (var update = connection.CreateCommand())
    {
      update.Transaction = (SqliteTransaction)transaction;
      update.CommandText = """
                UPDATE collection_runs
                SET state = 'Preparing', finished_at = NULL, can_resume = 0
                WHERE id = $id AND can_resume = 1;
                """;
      update.Parameters.AddWithValue("$id", id.ToString());
      try
      {
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
          throw new CollectionOpenException(
              CollectionErrorCode.RunNotRecoverable,
              "采集任务已不能恢复。");
        }
      }
      catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
      {
        throw new CollectionOpenException(
            CollectionErrorCode.EngineBusy,
            "已有采集任务正在运行。",
            ex);
      }
    }

    await transaction.CommitAsync(cancellationToken);
    return JsonSerializer.Deserialize<CollectionPlan>(planJson)
        ?? throw new CollectionOpenException(
            CollectionErrorCode.InvalidPlan,
            "保存的采集计划无法读取。");
  }

  public async Task SetStateAsync(
      CollectionRunId id,
      RunState state,
      RunCounters counters,
      bool canResume,
      CancellationToken cancellationToken = default)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            UPDATE collection_runs
            SET state = $state,
                finished_at = $finished,
                pages_discovered = $pages_discovered,
                pages_committed = $pages_committed,
                pages_failed = $pages_failed,
                threads_discovered = $threads_discovered,
                threads_committed = $threads_committed,
                threads_failed = $threads_failed,
                can_resume = $can_resume
            WHERE id = $id;
            """;
    command.Parameters.AddWithValue("$id", id.ToString());
    command.Parameters.AddWithValue("$state", state.ToString());
    command.Parameters.AddWithValue("$finished", IsTerminal(state)
        ? DateTimeOffset.UtcNow.ToString("O")
        : DBNull.Value);
    command.Parameters.AddWithValue("$pages_discovered", counters.PagesDiscovered);
    command.Parameters.AddWithValue("$pages_committed", counters.PagesCommitted);
    command.Parameters.AddWithValue("$pages_failed", counters.PagesFailed);
    command.Parameters.AddWithValue("$threads_discovered", counters.ThreadsDiscovered);
    command.Parameters.AddWithValue("$threads_committed", counters.ThreadsCommitted);
    command.Parameters.AddWithValue("$threads_failed", counters.ThreadsFailed);
    command.Parameters.AddWithValue("$can_resume", canResume ? 1 : 0);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  public async Task UpdateCountersAsync(
      CollectionRunId id,
      RunCounters counters,
      CancellationToken cancellationToken = default)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            UPDATE collection_runs
            SET pages_discovered = $pages_discovered,
                pages_committed = $pages_committed,
                pages_failed = $pages_failed,
                threads_discovered = $threads_discovered,
                threads_committed = $threads_committed,
                threads_failed = $threads_failed
            WHERE id = $id;
            """;
    command.Parameters.AddWithValue("$id", id.ToString());
    command.Parameters.AddWithValue("$pages_discovered", counters.PagesDiscovered);
    command.Parameters.AddWithValue("$pages_committed", counters.PagesCommitted);
    command.Parameters.AddWithValue("$pages_failed", counters.PagesFailed);
    command.Parameters.AddWithValue("$threads_discovered", counters.ThreadsDiscovered);
    command.Parameters.AddWithValue("$threads_committed", counters.ThreadsCommitted);
    command.Parameters.AddWithValue("$threads_failed", counters.ThreadsFailed);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  public async Task<bool> TryRequestCancelAsync(
      CollectionRunId id,
      RunCounters counters,
      CancellationToken cancellationToken = default)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            UPDATE collection_runs
            SET state = 'CancelRequested',
                pages_discovered = $pages_discovered,
                pages_committed = $pages_committed,
                pages_failed = $pages_failed,
                threads_discovered = $threads_discovered,
                threads_committed = $threads_committed,
                threads_failed = $threads_failed,
                can_resume = 1
            WHERE id = $id
              AND state IN ('Preparing', 'WaitingForUser', 'Discovering', 'Collecting');
            """;
    command.Parameters.AddWithValue("$id", id.ToString());
    command.Parameters.AddWithValue("$pages_discovered", counters.PagesDiscovered);
    command.Parameters.AddWithValue("$pages_committed", counters.PagesCommitted);
    command.Parameters.AddWithValue("$pages_failed", counters.PagesFailed);
    command.Parameters.AddWithValue("$threads_discovered", counters.ThreadsDiscovered);
    command.Parameters.AddWithValue("$threads_committed", counters.ThreadsCommitted);
    command.Parameters.AddWithValue("$threads_failed", counters.ThreadsFailed);
    return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
  }

  public async Task<RunSummary?> FindResumeCandidateAsync(CancellationToken cancellationToken)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            SELECT id, state, started_at, finished_at,
                   pages_discovered, pages_committed, pages_failed,
                   threads_discovered, threads_committed, threads_failed,
                   can_resume
            FROM collection_runs
            WHERE can_resume = 1
            ORDER BY started_at DESC
            LIMIT 1;
            """;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
      return null;
    }

    return new RunSummary(
        new CollectionRunId(Guid.ParseExact(reader.GetString(0), "N")),
        Enum.Parse<RunState>(reader.GetString(1)),
        DateTimeOffset.Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
        reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
        reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9),
        reader.GetInt32(10) != 0);
  }

  public async Task<RunCheckpoint> LoadCheckpointAsync(
      CollectionRunId runId,
      CancellationToken cancellationToken)
  {
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);

    RunCounters counters;
    await using (var command = connection.CreateCommand())
    {
      command.CommandText = """
                SELECT pages_discovered, pages_committed, pages_failed,
                       threads_discovered, threads_committed, threads_failed
                FROM collection_runs WHERE id = $id;
                """;
      command.Parameters.AddWithValue("$id", runId.ToString());
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      if (!await reader.ReadAsync(cancellationToken))
      {
        throw new CollectionOpenException(CollectionErrorCode.RunNotFound, "采集任务不存在。");
      }
      counters = new RunCounters
      {
        PagesDiscovered = reader.GetInt32(0),
        PagesCommitted = reader.GetInt32(1),
        PagesFailed = reader.GetInt32(2),
        ThreadsDiscovered = reader.GetInt32(3),
        ThreadsCommitted = reader.GetInt32(4),
        ThreadsFailed = reader.GetInt32(5)
      };
    }

    var frontier = await ReadItemsAsync(connection, "collection_frontier", runId, cancellationToken);
    var workItems = await ReadItemsAsync(connection, "collection_work_items", runId, cancellationToken);
    counters.PagesDiscovered = frontier.Count;
    counters.PagesCommitted = frontier.Count(item => item.State is "committed" or "terminal");
    counters.PagesFailed = frontier.Count(item => item.State == "failed");
    counters.ThreadsDiscovered = workItems.Count;
    counters.ThreadsCommitted = workItems.Count(item => item.State == "committed");
    counters.ThreadsFailed = workItems.Count(item => item.State == "failed");
    return new RunCheckpoint(counters, frontier, workItems);
  }

  public Task UpsertFrontierAsync(
      CollectionRunId runId,
      string logicalKey,
      Uri uri,
      CancellationToken cancellationToken) =>
      UpsertItemAsync(
          "collection_frontier",
          runId,
          logicalKey,
          uri,
          "pending",
          cancellationToken);

  public Task SetFrontierStateAsync(
      CollectionRunId runId,
      string logicalKey,
      string state,
      CancellationToken cancellationToken) =>
      SetItemStateAsync("collection_frontier", runId, logicalKey, state, cancellationToken);

  public Task UpsertWorkItemAsync(
      CollectionRunId runId,
      FavoriteReference favorite,
      CancellationToken cancellationToken) =>
      UpsertWorkItemCoreAsync(runId, favorite, cancellationToken);

  public Task SetWorkItemStateAsync(
      CollectionRunId runId,
      ThreadKey key,
      string state,
      CancellationToken cancellationToken) =>
      SetItemStateAsync("collection_work_items", runId, key.ToString(), state, cancellationToken);

  private async Task UpsertWorkItemCoreAsync(
      CollectionRunId runId,
      FavoriteReference favorite,
      CancellationToken cancellationToken)
  {
    var contextJson = JsonSerializer.Serialize(new FavoriteWorkContext
    {
      Title = favorite.Title,
      RawFavoriteTime = favorite.RawFavoriteTime,
      FavoriteTime = favorite.FavoriteTime,
      SourcePageUrl = favorite.SourcePageUri?.AbsoluteUri
    });
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
            INSERT INTO collection_work_items (
                run_id, logical_key, uri, state, work_parameters_json)
            VALUES ($run_id, $logical_key, $uri, 'pending', $work_parameters_json)
            ON CONFLICT(run_id, logical_key) DO UPDATE SET
                uri = excluded.uri,
                state = CASE
                    WHEN collection_work_items.state = 'committed'
                        THEN collection_work_items.state
                    ELSE excluded.state
                END,
                work_parameters_json = CASE
                    WHEN collection_work_items.state IN ('captured', 'committed')
                        THEN collection_work_items.work_parameters_json
                    ELSE excluded.work_parameters_json
                END;
            """;
    command.Parameters.AddWithValue("$run_id", runId.ToString());
    command.Parameters.AddWithValue("$logical_key", favorite.Key.ToString());
    command.Parameters.AddWithValue("$uri", favorite.Uri.AbsoluteUri);
    command.Parameters.AddWithValue("$work_parameters_json", contextJson);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task UpsertItemAsync(
      string table,
      CollectionRunId runId,
      string logicalKey,
      Uri uri,
      string state,
      CancellationToken cancellationToken)
  {
    var safeTable = table switch
    {
      "collection_frontier" => table,
      "collection_work_items" => table,
      _ => throw new ArgumentOutOfRangeException(nameof(table))
    };
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
            INSERT INTO {safeTable} (run_id, logical_key, uri, state)
            VALUES ($run_id, $logical_key, $uri, $state)
            ON CONFLICT(run_id, logical_key) DO UPDATE SET
                uri = excluded.uri,
                state = CASE
                    WHEN {safeTable}.state IN ('committed', 'terminal') THEN {safeTable}.state
                    ELSE excluded.state
                END;
            """;
    command.Parameters.AddWithValue("$run_id", runId.ToString());
    command.Parameters.AddWithValue("$logical_key", logicalKey);
    command.Parameters.AddWithValue("$uri", uri.AbsoluteUri);
    command.Parameters.AddWithValue("$state", state);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task SetItemStateAsync(
      string table,
      CollectionRunId runId,
      string logicalKey,
      string state,
      CancellationToken cancellationToken)
  {
    if (state is not ("pending" or "processing" or "committed" or "terminal" or "captured" or "failed"))
    {
      throw new ArgumentOutOfRangeException(nameof(state));
    }
    var safeTable = table switch
    {
      "collection_frontier" => table,
      "collection_work_items" => table,
      _ => throw new ArgumentOutOfRangeException(nameof(table))
    };
    await using var connection = CreateConnection();
    await connection.OpenAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
            UPDATE {safeTable}
            SET state = $state
            WHERE run_id = $run_id AND logical_key = $logical_key;
            """;
    command.Parameters.AddWithValue("$run_id", runId.ToString());
    command.Parameters.AddWithValue("$logical_key", logicalKey);
    command.Parameters.AddWithValue("$state", state);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private static async Task<IReadOnlyList<StoredRunItem>> ReadItemsAsync(
      SqliteConnection connection,
      string table,
      CollectionRunId runId,
      CancellationToken cancellationToken)
  {
    var safeTable = table switch
    {
      "collection_frontier" => table,
      "collection_work_items" => table,
      _ => throw new ArgumentOutOfRangeException(nameof(table))
    };
    await using var command = connection.CreateCommand();
    var contextProjection = safeTable == "collection_work_items"
        ? "work_parameters_json"
        : "NULL";
    command.CommandText = $"""
            SELECT logical_key, uri, state, {contextProjection}
            FROM {safeTable}
            WHERE run_id = $run_id
            ORDER BY logical_key;
            """;
    command.Parameters.AddWithValue("$run_id", runId.ToString());
    var items = new List<StoredRunItem>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      items.Add(new StoredRunItem(
          reader.GetString(0),
          new Uri(reader.GetString(1)),
          reader.GetString(2),
          ReadFavoriteContext(reader.IsDBNull(3) ? null : reader.GetString(3))));
    }
    return items;
  }

  private SqliteConnection CreateConnection()
  {
    return _database.CreateConnection();
  }

  private static FavoriteWorkContext? ReadFavoriteContext(string? json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return null;
    }

    try
    {
      return JsonSerializer.Deserialize<FavoriteWorkContext>(json);
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static bool IsTerminal(RunState state) => state is
      RunState.Cancelled or RunState.Succeeded or RunState.PartiallySucceeded or
      RunState.Interrupted or RunState.Failed;
}

internal sealed class RunCounters
{
  public int PagesDiscovered { get; set; }
  public int PagesCommitted { get; set; }
  public int PagesFailed { get; set; }
  public int ThreadsDiscovered { get; set; }
  public int ThreadsCommitted { get; set; }
  public int ThreadsFailed { get; set; }
}

internal sealed record StoredRunItem(
    string LogicalKey,
    Uri Uri,
    string State,
    FavoriteWorkContext? FavoriteContext);

internal sealed record FavoriteWorkContext
{
  public string? Title { get; init; }
  public string? RawFavoriteTime { get; init; }
  public DateTimeOffset? FavoriteTime { get; init; }
  public string? SourcePageUrl { get; init; }
}

internal sealed record RunCheckpoint(
    RunCounters Counters,
    IReadOnlyList<StoredRunItem> Frontier,
    IReadOnlyList<StoredRunItem> WorkItems);
