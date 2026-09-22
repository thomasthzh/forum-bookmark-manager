using System.Collections.Immutable;
using System.Collections.Concurrent;
using Rfbm.Collection.Acquisition;
using Rfbm.Collection.Adapters;
using Rfbm.Collection.Contracts;
using Rfbm.Collection.Media;
using Rfbm.Collection.Network;
using Rfbm.Data.Storage;

namespace Rfbm.Collection.Engine;

public sealed class CollectionEngine : ICollectionEngine
{
  private static readonly ConcurrentDictionary<string, SemaphoreSlim> OpenGates =
      new(StringComparer.OrdinalIgnoreCase);
  private static readonly ConcurrentDictionary<string, CollectionRunId> LiveRuns =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly IPageAcquisitionAdapter _acquisition;
  private readonly IForumSourceAdapter _forum;
  private readonly HttpClient _imageHttpClient;
  private readonly INetworkRecoveryAdapter? _networkRecovery;

  public CollectionEngine(
      IPageAcquisitionAdapter acquisition,
      IForumSourceAdapter forum,
      HttpClient? imageHttpClient = null,
      INetworkRecoveryAdapter? networkRecovery = null)
  {
    _acquisition = acquisition;
    _forum = forum;
    _imageHttpClient = imageHttpClient ?? SafeImageHttpClient.Shared;
    _networkRecovery = networkRecovery;
  }

  public async ValueTask<ICollectionSession> OpenAsync(
      CollectionIntent intent,
      CancellationToken cancellationToken = default)
  {
    var databasePath = intent switch
    {
      StartCollection startIntent => startIntent.Plan.DatabasePath,
      ResumeCollection resume => resume.DatabasePath,
      _ => throw new CollectionOpenException(
          CollectionErrorCode.InvalidPlan,
          "未知的采集意图。")
    };
    var databaseKey = Path.GetFullPath(databasePath);
    var openGate = OpenGates.GetOrAdd(databaseKey, _ => new SemaphoreSlim(1, 1));
    await openGate.WaitAsync(cancellationToken);

    CollectionRunId runId = default;
    CollectionPlan plan;
    RunCheckpoint? checkpoint = null;
    var store = new CollectionRunStore(databaseKey);
    CollectionRunLease? runLease = null;
    var registeredLiveRun = false;
    try
    {
      if (LiveRuns.ContainsKey(databaseKey) ||
          !CollectionRunLease.TryAcquire(databaseKey, out runLease))
      {
        throw new CollectionOpenException(
            CollectionErrorCode.EngineBusy,
            "已有采集任务正在运行。");
      }

      await store.InitializeAsync(cancellationToken);
      await store.RecoverAbandonedRunsAsync(cancellationToken);

      if (intent is StartCollection start)
      {
        plan = start.Plan with { DatabasePath = databaseKey };
        plan.Validate();
        runId = new CollectionRunId(Guid.NewGuid());
        await store.CreateAsync(runId, plan, cancellationToken);
      }
      else
      {
        var resume = (ResumeCollection)intent;
        runId = resume.RunId;
        plan = await store.ReactivateAsync(runId, cancellationToken);
        plan.Validate();
        checkpoint = await store.LoadCheckpointAsync(runId, cancellationToken);
      }

      if (!LiveRuns.TryAdd(databaseKey, runId))
      {
        throw new CollectionOpenException(
            CollectionErrorCode.EngineBusy,
            "已有采集任务正在运行。");
      }
      registeredLiveRun = true;
    }
    catch
    {
      if (registeredLiveRun)
      {
        LiveRuns.TryRemove(new KeyValuePair<string, CollectionRunId>(databaseKey, runId));
      }
      runLease?.Dispose();
      throw;
    }
    finally
    {
      openGate.Release();
    }

    var runCancellation = new CancellationTokenSource();
    var counters = checkpoint?.Counters ?? new RunCounters();
    var session = new CollectionSession(runId, async requestToken =>
    {
      if (await store.TryRequestCancelAsync(runId, counters, requestToken))
      {
        runCancellation.Cancel();
      }
    });

    session.Completion = Task.Run(
        () => RunAsync(
            session,
            store,
            plan,
            counters,
            checkpoint,
            databaseKey,
            runLease!,
            runCancellation.Token),
        CancellationToken.None);
    return session;
  }

  public async ValueTask<RunSummary?> FindResumeCandidateAsync(
      string databasePath,
      CancellationToken cancellationToken = default)
  {
    var databaseKey = Path.GetFullPath(databasePath);
    var openGate = OpenGates.GetOrAdd(databaseKey, _ => new SemaphoreSlim(1, 1));
    await openGate.WaitAsync(cancellationToken);
    try
    {
      if (LiveRuns.ContainsKey(databaseKey) ||
          !CollectionRunLease.TryAcquire(databaseKey, out var lease))
      {
        return null;
      }

      using (lease)
      {
        var store = new CollectionRunStore(databaseKey);
        await store.InitializeAsync(cancellationToken);
        await store.RecoverAbandonedRunsAsync(cancellationToken);
        return await store.FindResumeCandidateAsync(cancellationToken);
      }
    }
    finally
    {
      openGate.Release();
    }
  }

  private async Task<CollectionResult> RunAsync(
      CollectionSession session,
      CollectionRunStore store,
      CollectionPlan plan,
      RunCounters counters,
      RunCheckpoint? checkpoint,
      string databaseKey,
      CollectionRunLease runLease,
      CancellationToken cancellationToken)
  {
    var sequence = 0L;
    var issues = new List<CollectionIssue>();
    var issueGate = new object();
    var counterGate = new object();
    using var userActionGate = new SemaphoreSlim(1, 1);
    var repository = new PostRepository(new BookmarkDatabase(plan.DatabasePath));
    var mediaDirectory = Path.GetFullPath(plan.MediaDirectory ?? Path.Combine(
        Path.GetDirectoryName(plan.DatabasePath)!,
        "images"));
    var imageStore = new AtomicImageStore(mediaDirectory, _imageHttpClient, plan.MaxImageBytes);
    using var imageGate = new SemaphoreSlim(plan.ImageConcurrency, plan.ImageConcurrency);
    using var progressPersistenceGate = new SemaphoreSlim(1, 1);
    var networkRecoverySync = new object();
    Task<NetworkRecoveryResult>? networkRecoveryTask = null;

    void AddIssue(CollectionIssue issue)
    {
      lock (issueGate)
      {
        issues.Add(issue);
      }
    }

    void State(RunState state, string message)
    {
      session.Publish(new StateChanged(
          session.Id,
          Interlocked.Increment(ref sequence),
          DateTimeOffset.UtcNow,
          state,
          message));
    }

    async Task ProgressAsync(CancellationToken progressCancellationToken)
    {
      await progressPersistenceGate.WaitAsync(progressCancellationToken);
      try
      {
        ProgressChanged progress;
        RunCounters persisted;
        lock (counterGate)
        {
          persisted = new RunCounters
          {
            PagesDiscovered = counters.PagesDiscovered,
            PagesCommitted = counters.PagesCommitted,
            PagesFailed = counters.PagesFailed,
            ThreadsDiscovered = counters.ThreadsDiscovered,
            ThreadsCommitted = counters.ThreadsCommitted,
            ThreadsFailed = counters.ThreadsFailed
          };
          progress = new ProgressChanged(
              session.Id,
              Interlocked.Increment(ref sequence),
              DateTimeOffset.UtcNow,
              persisted.PagesDiscovered,
              persisted.PagesCommitted,
              persisted.PagesFailed,
              persisted.ThreadsDiscovered,
              persisted.ThreadsCommitted,
              persisted.ThreadsFailed);
        }
        await store.UpdateCountersAsync(
            session.Id,
            persisted,
            progressCancellationToken);
        session.Publish(progress);
      }
      finally
      {
        progressPersistenceGate.Release();
      }
    }

    CollectionResult Result(RunState state, bool canResume) => new(
        session.Id,
        state,
        counters.PagesDiscovered,
        counters.PagesCommitted,
        counters.PagesFailed,
        counters.ThreadsDiscovered,
        counters.ThreadsCommitted,
        counters.ThreadsFailed,
        issues.ToImmutableArray(),
        canResume);

    async Task<PageAcquisitionResult> AcquireWithUserResolutionAsync(
        PageRequest request,
        RunState returnState,
        CancellationToken acquisitionCancellationToken)
    {
      var acquisition = await _acquisition.AcquireAsync(request, acquisitionCancellationToken);
      for (var attempt = 0; attempt < 2 && acquisition is PageNeedsUser prompt; attempt++)
      {
        await userActionGate.WaitAsync(acquisitionCancellationToken);
        try
        {
          acquisition = await _acquisition.AcquireAsync(request, acquisitionCancellationToken);
          if (acquisition is not PageNeedsUser refreshedPrompt)
          {
            return acquisition;
          }
          prompt = refreshedPrompt;

          State(RunState.WaitingForUser, prompt.SafeMessage);
          await store.SetStateAsync(
              session.Id,
              RunState.WaitingForUser,
              counters,
              true,
              acquisitionCancellationToken);

          if (_acquisition is not IInteractivePageAcquisitionAdapter interactive ||
              !await interactive.ResolveUserActionAsync(
                  request,
                  prompt,
                  acquisitionCancellationToken))
          {
            return acquisition;
          }

          acquisitionCancellationToken.ThrowIfCancellationRequested();
          State(returnState, returnState == RunState.Discovering
              ? "人工验证完成，继续检查收藏分页"
              : "人工验证完成，继续采集帖子");
          await store.SetStateAsync(
              session.Id,
              returnState,
              counters,
              false,
              acquisitionCancellationToken);
          acquisition = await _acquisition.AcquireAsync(
              request,
              acquisitionCancellationToken);
        }
        finally
        {
          userActionGate.Release();
        }
      }

      return acquisition;
    }

    async Task<PageAcquisitionResult> AcquireWithNetworkRoutesAsync(
        PageRequest logicalRequest,
        RunState returnState,
        CancellationToken acquisitionCancellationToken)
    {
      async Task<PageAcquisitionResult> TryAllOriginsAsync()
      {
        PageAcquisitionResult? last = null;
        foreach (var routedUri in RouteCandidates(logicalRequest.Uri, plan.Site))
        {
          var routedRequest = logicalRequest with { Uri = routedUri };
          var acquisition = await AcquireWithUserResolutionAsync(
              routedRequest,
              returnState,
              acquisitionCancellationToken);
          if (acquisition is not PageUnavailable unavailable ||
              !IsRetryableNavigationFailure(unavailable.Issue))
          {
            return acquisition;
          }
          last = acquisition;
        }

        return last ?? new PageUnavailable(CollectionIssue.Resumable(
            CollectionErrorCode.NavigationFailed,
            CollectionStage.Acquire,
            "没有可用的站点路由。",
            logicalRequest.Uri,
            logicalRequest.ExpectedThread));
      }

      var result = await TryAllOriginsAsync();
      if (result is not PageUnavailable unavailable ||
          !IsRetryableNavigationFailure(unavailable.Issue) ||
          _networkRecovery is null)
      {
        return result;
      }

      Task<NetworkRecoveryResult> recovery;
      lock (networkRecoverySync)
      {
        networkRecoveryTask ??= _networkRecovery.TryRecoverAsync(
            new NetworkFailureContext(logicalRequest.Uri, unavailable.Issue.SafeMessage),
            acquisitionCancellationToken).AsTask();
        recovery = networkRecoveryTask;
      }

      NetworkRecoveryResult recovered;
      try
      {
        recovered = await recovery;
      }
      catch (OperationCanceledException) when (acquisitionCancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception)
      {
        return result;
      }
      if (!recovered.Changed)
      {
        return result;
      }

      acquisitionCancellationToken.ThrowIfCancellationRequested();
      State(returnState, recovered.SafeMessage);
      return await TryAllOriginsAsync();
    }

    async Task<bool> DownloadImagesAsync(
        Rfbm.Core.Models.BookmarkPost post,
        StoredCaptureSnapshot capture,
        CancellationToken workerCancellationToken)
    {
      if (!plan.DownloadPostImages || post.Images.Count == 0)
      {
        return true;
      }

      var dataKey = new PostKey(post.Key.SiteFamily, post.Key.ThreadId);
      var tasks = post.Images.Take(9).Select(async image =>
      {
        await imageGate.WaitAsync(workerCancellationToken);
        try
        {
          var saved = await imageStore.SaveAsync(
                    new Uri(image.SourceUrl),
                    new ThreadKey(new SiteFamily(post.Key.SiteFamily), post.Key.ThreadId),
                    image.Position,
                    $"generation-{capture.CaptureGeneration:D20}-snapshot-{capture.SnapshotId:D20}",
                    workerCancellationToken);
          if (!await repository.UpdateImageDownloadAsync(
                    new ImageDownloadUpdateRequest
                    {
                      PostKey = dataKey,
                      SnapshotId = capture.SnapshotId,
                      CaptureGeneration = capture.CaptureGeneration,
                      SourceUrl = image.SourceUrl,
                      LocalPath = saved.RelativePath,
                      ThumbnailPath = null,
                      DownloadStatus = "downloaded"
                    },
                    workerCancellationToken))
          {
            throw new InvalidOperationException(
                "The downloaded image no longer belongs to the active capture generation.");
          }
          return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or HttpRequestException ||
            (ex is OperationCanceledException && !workerCancellationToken.IsCancellationRequested))
        {
          if (!await repository.UpdateImageDownloadAsync(
                    new ImageDownloadUpdateRequest
                    {
                      PostKey = dataKey,
                      SnapshotId = capture.SnapshotId,
                      CaptureGeneration = capture.CaptureGeneration,
                      SourceUrl = image.SourceUrl,
                      LocalPath = null,
                      ThumbnailPath = null,
                      DownloadStatus = "failed"
                    },
                    workerCancellationToken))
          {
            throw new InvalidOperationException(
                "The rejected image no longer belongs to the active capture generation.");
          }
          var issue = CollectionIssue.Resumable(
                    CollectionErrorCode.ImageRejected,
                    CollectionStage.DownloadImage,
                    "一张帖子图片未能安全保存。",
                    new Uri(image.SourceUrl),
                    new ThreadKey(new SiteFamily(post.Key.SiteFamily), post.Key.ThreadId));
          AddIssue(issue);
          session.Publish(new ItemFailed(
                    session.Id,
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    issue.Uri,
                    issue.Thread,
                    issue));
          return false;
        }
        finally
        {
          imageGate.Release();
        }
      });
      var results = await Task.WhenAll(tasks);
      return results.All(static result => result);
    }

    try
    {
      State(RunState.Discovering, "正在验证收藏分页");
      await store.SetStateAsync(session.Id, RunState.Discovering, counters, false, cancellationToken);

      var frontier = new PaginationFrontier();
      var threadReferences = new Dictionary<ThreadKey, FavoriteReference>();
      var frontierStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var workStates = new Dictionary<ThreadKey, string>();
      var observedTerminalEvidence = false;
      string? lastCommittedPageKey = null;
      Uri? lastCommittedPageUri = null;
      if (checkpoint is null)
      {
        frontier.Add(plan.FavoritesUrl);
        await store.UpsertFrontierAsync(
            session.Id,
            PaginationFrontier.LogicalKey(plan.FavoritesUrl),
            plan.FavoritesUrl,
            cancellationToken);
        counters.PagesDiscovered = frontier.Count;
      }
      else
      {
        var hasDurableTerminalEvidence = checkpoint.Frontier.Any(
            item => item.State == "terminal");
        var revalidateLegacyTerminal = !hasDurableTerminalEvidence &&
            checkpoint.Frontier.Count > 0 &&
            checkpoint.Frontier.All(item => item.State == "committed");
        var legacyTerminalKey = revalidateLegacyTerminal
            ? checkpoint.Frontier
                .MaxBy(item => PaginationFrontier.LogicalPageNumber(item.Uri))!
                .LogicalKey
            : null;
        foreach (var item in checkpoint.Frontier)
        {
          frontierStates[item.LogicalKey] = item.State;
          if (item.State == "terminal")
          {
            observedTerminalEvidence = true;
            frontier.Remember(item.Uri);
          }
          else if (item.State == "committed" && item.LogicalKey != legacyTerminalKey)
          {
            frontier.Remember(item.Uri);
          }
          else
          {
            frontier.Add(item.Uri);
          }
        }

        foreach (var item in checkpoint.WorkItems)
        {
          var separator = item.LogicalKey.IndexOf(':');
          if (separator <= 0 || separator == item.LogicalKey.Length - 1)
          {
            continue;
          }
          var key = new ThreadKey(
              new SiteFamily(item.LogicalKey[..separator]),
              item.LogicalKey[(separator + 1)..]);
          var favoriteContext = item.FavoriteContext;
          threadReferences[key] = new FavoriteReference(
              key,
              item.Uri,
              favoriteContext?.Title,
              favoriteContext?.RawFavoriteTime,
              favoriteContext?.FavoriteTime,
              Uri.TryCreate(
                  favoriteContext?.SourcePageUrl,
                  UriKind.Absolute,
                  out var sourcePageUri)
                  ? sourcePageUri
                  : null);
          workStates[key] = item.State;
        }
      }

      var scheduledFavoritePages = new Queue<(
          Uri Uri,
          string LogicalKey,
          Task<PageAcquisitionResult> Acquisition)>();
      using var favoritePrefetchCancellation =
          CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      try
      {
        while (frontier.HasPending || scheduledFavoritePages.Count > 0)
        {
          cancellationToken.ThrowIfCancellationRequested();
          while (frontier.HasPending &&
                 scheduledFavoritePages.Count < plan.FavoritePageConcurrency)
          {
            var scheduledUri = frontier.Take();
            var scheduledKey = PaginationFrontier.LogicalKey(scheduledUri);
            if (frontierStates.TryGetValue(scheduledKey, out var previousPageState) &&
                previousPageState == "failed")
            {
              counters.PagesFailed = Math.Max(0, counters.PagesFailed - 1);
            }
            else if (previousPageState == "committed")
            {
              counters.PagesCommitted = Math.Max(0, counters.PagesCommitted - 1);
            }
            frontierStates[scheduledKey] = "processing";
            await store.SetFrontierStateAsync(
                session.Id,
                scheduledKey,
                "processing",
                cancellationToken);
            var scheduledRequest = new PageRequest(
                session.Id,
                PageKind.FavoriteIndex,
                scheduledUri,
                null,
                plan.MaxSnapshotBytes);
            scheduledFavoritePages.Enqueue((
                scheduledUri,
                scheduledKey,
                AcquireWithNetworkRoutesAsync(
                    scheduledRequest,
                    RunState.Discovering,
                    favoritePrefetchCancellation.Token)));
          }

          var scheduled = scheduledFavoritePages.Dequeue();
          var pageUri = scheduled.Uri;
          var pageKey = scheduled.LogicalKey;
          var acquisition = await scheduled.Acquisition;

          if (acquisition is PageNeedsUser needsUser)
          {
            var issue = new CollectionIssue(
                needsUser.Code,
                CollectionStage.Acquire,
                ErrorDisposition.NeedsUser,
                needsUser.SafeMessage,
                needsUser.Uri);
            AddIssue(issue);
            counters.PagesFailed++;
            await store.SetFrontierStateAsync(session.Id, pageKey, "failed", cancellationToken);
            session.Publish(new ItemFailed(session.Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, pageUri, null, issue));
            await ProgressAsync(cancellationToken);
            continue;
          }

          if (acquisition is PageUnavailable unavailable)
          {
            var issue = CollectionIssue.Resumable(
                CollectionErrorCode.PaginationIncomplete,
                CollectionStage.DiscoverPagination,
                unavailable.Issue.SafeMessage,
                pageUri);
            AddIssue(issue);
            counters.PagesFailed++;
            await store.SetFrontierStateAsync(session.Id, pageKey, "failed", cancellationToken);
            session.Publish(new ItemFailed(session.Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, pageUri, null, issue));
            await ProgressAsync(cancellationToken);
            continue;
          }

          var captured = (PageCaptured)acquisition;
          FavoritePageProjection projection;
          try
          {
            VerifySnapshot(captured.Snapshot, pageUri, PageKind.FavoriteIndex, plan);
            projection = _forum.ParseFavoritePage(captured.Snapshot, plan.Site);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
            var issue = CollectionIssue.Resumable(
                ex is InvalidDataException
                    ? CollectionErrorCode.SnapshotRejected
                    : CollectionErrorCode.ParseFailed,
                ex is InvalidDataException
                    ? CollectionStage.VerifySnapshot
                    : CollectionStage.Parse,
                ex is InvalidDataException
                    ? "收藏页快照未通过来源或完整性校验。"
                    : "收藏页结构无法解析。",
                pageUri);
            AddIssue(issue);
            counters.PagesFailed++;
            await store.SetFrontierStateAsync(session.Id, pageKey, "failed", cancellationToken);
            session.Publish(new ItemFailed(session.Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, pageUri, null, issue));
            await ProgressAsync(cancellationToken);
            continue;
          }

          var requestedLogicalPage = PaginationFrontier.LogicalPageNumber(pageUri);
          if (projection.CurrentPage is not { } projectedCurrentPage ||
              projectedCurrentPage != requestedLogicalPage)
          {
            var issue = CollectionIssue.Resumable(
                CollectionErrorCode.PaginationIncomplete,
                CollectionStage.DiscoverPagination,
                "收藏页返回的页码与请求逻辑页不一致。",
                pageUri);
            AddIssue(issue);
            counters.PagesFailed++;
            await store.SetFrontierStateAsync(session.Id, pageKey, "failed", cancellationToken);
            session.Publish(new ItemFailed(session.Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, pageUri, null, issue));
            await ProgressAsync(cancellationToken);
            continue;
          }

          if (projection.DeclaredLastPage is { } declaredLast)
          {
            if (declaredLast < 1 || declaredLast > plan.MaxFavoritePages ||
                projectedCurrentPage > declaredLast)
            {
              var issue = CollectionIssue.Resumable(
                  CollectionErrorCode.PaginationIncomplete,
                  CollectionStage.DiscoverPagination,
                  "收藏分页声明互相矛盾。",
                  pageUri);
              AddIssue(issue);
              counters.PagesFailed++;
              await store.SetFrontierStateAsync(session.Id, pageKey, "failed", cancellationToken);
              session.Publish(new ItemFailed(session.Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, pageUri, null, issue));
              await ProgressAsync(cancellationToken);
              continue;
            }

            for (var page = 1; page <= declaredLast; page++)
            {
              var declaredUri = PaginationFrontier.WithPage(plan.FavoritesUrl, page);
              if (frontier.Add(declaredUri))
              {
                frontierStates[PaginationFrontier.LogicalKey(declaredUri)] = "pending";
                await store.UpsertFrontierAsync(
                    session.Id,
                    PaginationFrontier.LogicalKey(declaredUri),
                    declaredUri,
                    cancellationToken);
              }
            }
          }

          if (projection.HasExplicitTerminalEvidence &&
              (projection.NextUri is not null ||
               projection.DeclaredLastPage is { } terminalLast && projectedCurrentPage < terminalLast ||
               projection.DiscoveredPageUris.Any(uri =>
                   PaginationFrontier.LogicalPageNumber(uri) > projectedCurrentPage)))
          {
            var issue = CollectionIssue.Resumable(
                CollectionErrorCode.PaginationIncomplete,
                CollectionStage.DiscoverPagination,
                "收藏分页同时声明终页和后续页面。",
                pageUri);
            AddIssue(issue);
            counters.PagesFailed++;
            await store.SetFrontierStateAsync(session.Id, pageKey, "failed", cancellationToken);
            session.Publish(new ItemFailed(session.Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, pageUri, null, issue));
            await ProgressAsync(cancellationToken);
            continue;
          }

          var pageHasTerminalEvidence = projection.HasExplicitTerminalEvidence ||
              projection.DeclaredLastPage == projectedCurrentPage;
          observedTerminalEvidence |= pageHasTerminalEvidence;

          if (projection.NextUri is { } nextUri)
          {
            var nextPage = PaginationFrontier.LogicalPageNumber(nextUri);
            if (!plan.Site.ContainsOrigin(nextUri) ||
                nextPage <= projectedCurrentPage ||
                nextPage > plan.MaxFavoritePages)
            {
              var issue = CollectionIssue.Resumable(
                  CollectionErrorCode.PaginationIncomplete,
                  CollectionStage.DiscoverPagination,
                  "收藏页的下一页证据无效。",
                  pageUri);
              AddIssue(issue);
              counters.PagesFailed++;
              await store.SetFrontierStateAsync(session.Id, pageKey, "failed", cancellationToken);
              session.Publish(new ItemFailed(session.Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, pageUri, null, issue));
              await ProgressAsync(cancellationToken);
              continue;
            }

            if (frontier.Add(nextUri))
            {
              frontierStates[PaginationFrontier.LogicalKey(nextUri)] = "pending";
              await store.UpsertFrontierAsync(
                  session.Id,
                  PaginationFrontier.LogicalKey(nextUri),
                  nextUri,
                  cancellationToken);
            }
          }

          foreach (var discovered in projection.DiscoveredPageUris)
          {
            if (!plan.Site.ContainsOrigin(discovered))
            {
              continue;
            }
            if (frontier.Add(discovered))
            {
              frontierStates[PaginationFrontier.LogicalKey(discovered)] = "pending";
              await store.UpsertFrontierAsync(
                  session.Id,
                  PaginationFrontier.LogicalKey(discovered),
                  discovered,
                  cancellationToken);
            }
          }
          counters.PagesDiscovered = frontier.Count;

          foreach (var thread in projection.Threads)
          {
            if (thread.Key.SiteFamily != plan.Site.Family || !plan.Site.ContainsOrigin(thread.Uri))
            {
              continue;
            }
            if (threadReferences.TryAdd(thread.Key, thread))
            {
              workStates[thread.Key] = "pending";
              await store.UpsertWorkItemAsync(session.Id, thread, cancellationToken);
            }
          }

          counters.PagesCommitted++;
          await store.SetFrontierStateAsync(
              session.Id,
              pageKey,
              pageHasTerminalEvidence ? "terminal" : "committed",
              cancellationToken);
          lastCommittedPageKey = pageKey;
          lastCommittedPageUri = pageUri;
          counters.ThreadsDiscovered = Math.Max(counters.ThreadsDiscovered, threadReferences.Count);
          await ProgressAsync(cancellationToken);
        }
      }
      finally
      {
        favoritePrefetchCancellation.Cancel();
        while (scheduledFavoritePages.TryDequeue(out var pendingFavoritePage))
        {
          try
          {
            await pendingFavoritePage.Acquisition;
          }
          catch (OperationCanceledException) when (favoritePrefetchCancellation.IsCancellationRequested)
          {
          }
          catch (Exception)
          {
          }
        }
      }

      if (!observedTerminalEvidence && issues.Count == 0 &&
          lastCommittedPageKey is not null && lastCommittedPageUri is not null)
      {
        var issue = CollectionIssue.Resumable(
            CollectionErrorCode.PaginationIncomplete,
            CollectionStage.DiscoverPagination,
            "收藏分页没有提供可验证的终页证据。",
            lastCommittedPageUri);
        AddIssue(issue);
        counters.PagesCommitted = Math.Max(0, counters.PagesCommitted - 1);
        counters.PagesFailed++;
        frontierStates[lastCommittedPageKey] = "failed";
        await store.SetFrontierStateAsync(
            session.Id,
            lastCommittedPageKey,
            "failed",
            cancellationToken);
        session.Publish(new ItemFailed(
            session.Id,
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            lastCommittedPageUri,
            null,
            issue));
        await ProgressAsync(cancellationToken);
      }

      State(RunState.Collecting, "正在采集帖子详情");
      await store.SetStateAsync(session.Id, RunState.Collecting, counters, issues.Count > 0, cancellationToken);

      var pendingThreads = threadReferences.Values.Where(thread =>
              !workStates.TryGetValue(thread.Key, out var state) || state != "committed")
          .ToArray();
      await Parallel.ForEachAsync(
          pendingThreads,
          new ParallelOptions
          {
            MaxDegreeOfParallelism = plan.ThreadConcurrency,
            CancellationToken = cancellationToken
          },
          async (thread, workerCancellationToken) =>
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (workStates.TryGetValue(thread.Key, out var previousWorkState) &&
                  previousWorkState == "failed")
        {
          lock (counterGate)
          {
            counters.ThreadsFailed = Math.Max(0, counters.ThreadsFailed - 1);
          }
        }
        await store.SetWorkItemStateAsync(session.Id, thread.Key, "processing", workerCancellationToken);
        var threadRequest = new PageRequest(
                  session.Id,
                  PageKind.ThreadDetail,
                  thread.Uri,
                  thread.Key,
                  plan.MaxSnapshotBytes);
        PageAcquisitionResult acquisition;
        try
        {
          acquisition = await AcquireWithNetworkRoutesAsync(
              threadRequest,
              RunState.Collecting,
              workerCancellationToken);
        }
        catch (OperationCanceledException) when (workerCancellationToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception)
        {
          var issue = CollectionIssue.Resumable(
              CollectionErrorCode.NavigationFailed,
              CollectionStage.Acquire,
              "帖子导航异常，本帖可稍后重试。",
              thread.Uri,
              thread.Key);
          AddIssue(issue);
          lock (counterGate)
          {
            counters.ThreadsFailed++;
          }
          await store.SetWorkItemStateAsync(
              session.Id,
              thread.Key,
              "failed",
              workerCancellationToken);
          session.Publish(new ItemFailed(
              session.Id,
              Interlocked.Increment(ref sequence),
              DateTimeOffset.UtcNow,
              thread.Uri,
              thread.Key,
              issue));
          await ProgressAsync(workerCancellationToken);
          return;
        }

        if (acquisition is not PageCaptured captured)
        {
          var sourceIssue = acquisition switch
          {
            PageUnavailable unavailable => unavailable.Issue,
            PageNeedsUser needsUser => new CollectionIssue(
                      needsUser.Code, CollectionStage.Acquire, ErrorDisposition.NeedsUser,
                      needsUser.SafeMessage, needsUser.Uri, thread.Key),
            _ => CollectionIssue.Permanent(
                      CollectionErrorCode.NavigationFailed, CollectionStage.Acquire,
                      "帖子页不可用。", thread.Uri, thread.Key)
          };
          AddIssue(sourceIssue);
          lock (counterGate)
          {
            counters.ThreadsFailed++;
          }
          await store.SetWorkItemStateAsync(session.Id, thread.Key, "failed", workerCancellationToken);
          session.Publish(new ItemFailed(
                    session.Id,
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    thread.Uri,
                    thread.Key,
                    sourceIssue));
          await ProgressAsync(workerCancellationToken);
          return;
        }

        try
        {
          VerifySnapshot(
              captured.Snapshot,
              thread.Uri,
              PageKind.ThreadDetail,
              plan,
              thread.Key);
          var projection = _forum is IFavoriteContextForumSourceAdapter contextAwareForum
                    ? contextAwareForum.ParseThreadPage(captured.Snapshot, thread)
                    : _forum.ParseThreadPage(captured.Snapshot, thread.Key);
          if (projection.Key != thread.Key)
          {
            throw new InvalidDataException("Thread identity mismatch.");
          }
          if (projection.CapturedPost is not null)
          {
            var capturedPost = projection.CapturedPost;
            var postCapture = CorePostCaptureMapper.Map(capturedPost);
            var mediaPending = plan.DownloadPostImages && capturedPost.Images.Count > 0;
            await repository.CommitCaptureAsync(
                      postCapture,
                      new CaptureSnapshotMetadata
                      {
                        RunId = session.Id.ToString(),
                        WorkItemKey = thread.Key.ToString(),
                        PostKey = postCapture.Key,
                        RequestedUrl = captured.Snapshot.RequestedUri.AbsoluteUri,
                        FinalUrl = captured.Snapshot.FinalUri.AbsoluteUri,
                        Kind = (captured.Snapshot.ClaimedKind ?? PageKind.ThreadDetail).ToString(),
                        Sha256 = captured.Snapshot.Sha256 ?? captured.Snapshot.ComputedSha256,
                        CapturedAt = captured.Snapshot.CapturedAt,
                        ByteCount = captured.Snapshot.Utf8Size,
                        MediaPending = mediaPending,
                        WorkParametersJson = mediaPending
                              ? $"{{\"remaining_images\":{Math.Min(9, capturedPost.Images.Count)}}}"
                              : null
                      },
                      workerCancellationToken);
            var capture = await repository.GetCaptureSnapshotAsync(
                session.Id.ToString(),
                thread.Key.ToString(),
                workerCancellationToken) ?? throw new InvalidOperationException(
                    "The atomic capture did not produce a durable snapshot binding.");
            if (await DownloadImagesAsync(capturedPost, capture, workerCancellationToken))
            {
              if (!await repository.FinalizeCaptureAsync(
                            new CaptureFinalizeRequest
                            {
                              RunId = session.Id.ToString(),
                              WorkItemKey = thread.Key.ToString(),
                              PostKey = postCapture.Key,
                              SnapshotId = capture.SnapshotId,
                              CaptureGeneration = capture.CaptureGeneration
                            },
                            workerCancellationToken))
              {
                throw new InvalidOperationException(
                          "A newly captured work item was already finalized.");
              }
              lock (counterGate)
              {
                counters.ThreadsCommitted++;
              }
            }
            else
            {
              await store.SetWorkItemStateAsync(
                        session.Id,
                        thread.Key,
                        "failed",
                        workerCancellationToken);
              lock (counterGate)
              {
                counters.ThreadsFailed++;
              }
            }
          }
          else
          {
            await store.SetWorkItemStateAsync(
                      session.Id,
                      thread.Key,
                      "committed",
                      workerCancellationToken);
            lock (counterGate)
            {
              counters.ThreadsCommitted++;
            }
          }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          var issue = CollectionIssue.Resumable(
                    ex is InvalidDataException
                        ? CollectionErrorCode.SnapshotRejected
                        : CollectionErrorCode.IdentityMismatch,
                    ex is InvalidDataException
                        ? CollectionStage.VerifySnapshot
                        : CollectionStage.Parse,
                    ex is InvalidDataException
                        ? "帖子快照未通过来源或完整性校验。"
                        : "帖子身份或结构校验失败。",
                    thread.Uri,
                    thread.Key);
          AddIssue(issue);
          lock (counterGate)
          {
            counters.ThreadsFailed++;
          }
          await store.SetWorkItemStateAsync(session.Id, thread.Key, "failed", workerCancellationToken);
          session.Publish(new ItemFailed(
                    session.Id,
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    thread.Uri,
                    thread.Key,
                    issue));
        }
        await ProgressAsync(workerCancellationToken);
      });

      var state = issues.Count == 0 ? RunState.Succeeded : RunState.PartiallySucceeded;
      var canResume = issues.Count > 0;
      State(state, state == RunState.Succeeded ? "采集完成" : "采集部分完成，可稍后恢复");
      await store.SetStateAsync(session.Id, state, counters, canResume, CancellationToken.None);
      return Result(state, canResume);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      var issue = CollectionIssue.Resumable(
          CollectionErrorCode.Cancelled,
          CollectionStage.Finalize,
          "采集已由用户取消。");
      AddIssue(issue);
      State(RunState.Cancelled, "采集已取消，可稍后恢复");
      await store.SetStateAsync(session.Id, RunState.Cancelled, counters, true, CancellationToken.None);
      return Result(RunState.Cancelled, true);
    }
    catch (Exception)
    {
      var issue = CollectionIssue.Resumable(
          CollectionErrorCode.PersistenceFailed,
          CollectionStage.Finalize,
          "采集因内部错误中断，可稍后恢复。");
      AddIssue(issue);
      State(RunState.Failed, "采集中断");
      await store.SetStateAsync(session.Id, RunState.Failed, counters, true, CancellationToken.None);
      return Result(RunState.Failed, true);
    }
    finally
    {
      LiveRuns.TryRemove(new KeyValuePair<string, CollectionRunId>(databaseKey, session.Id));
      runLease.Dispose();
      session.CompleteEvents();
    }
  }

  private static IEnumerable<Uri> RouteCandidates(Uri logicalUri, SiteDefinition site)
  {
    var origins = site.MirrorOrigins.Prepend(site.PrimaryOrigin)
        .OrderByDescending(origin => SameOrigin(origin, logicalUri));
    foreach (var origin in origins)
    {
      yield return new Uri(origin, logicalUri.PathAndQuery + logicalUri.Fragment);
    }
  }

  private static bool IsRetryableNavigationFailure(CollectionIssue issue) =>
      issue.Code == CollectionErrorCode.NavigationFailed &&
      issue.Disposition is ErrorDisposition.Retryable or ErrorDisposition.Resumable;

  private static bool SameOrigin(Uri left, Uri right) =>
      string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
      string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
      left.Port == right.Port;

  private static void VerifySnapshot(
      PageSnapshot snapshot,
      Uri logicalUri,
      PageKind expectedKind,
      CollectionPlan plan,
      ThreadKey? expectedThread = null)
  {
    if (!snapshot.RequestedUri.IsAbsoluteUri ||
        !plan.Site.ContainsOrigin(snapshot.RequestedUri) ||
        !string.Equals(snapshot.RequestedUri.PathAndQuery, logicalUri.PathAndQuery, StringComparison.Ordinal) ||
        !snapshot.FinalUri.IsAbsoluteUri || !plan.Site.ContainsOrigin(snapshot.FinalUri) ||
        snapshot.Utf8Size > plan.MaxSnapshotBytes ||
        !snapshot.MediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(snapshot.Document) ||
        snapshot.ClaimedKind is not null && snapshot.ClaimedKind != expectedKind ||
        snapshot.Sha256 is not null && !string.Equals(
            snapshot.Sha256,
            snapshot.ComputedSha256,
            StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidDataException($"Rejected snapshot for {logicalUri.Host}.");
    }

    var pageValidation = Rfbm.Core.Parsing.PageValidation.ValidateCollection(
        snapshot.Document,
        snapshot.FinalUri);
    if (!pageValidation.IsValid ||
        !MatchesLogicalTarget(snapshot.FinalUri, logicalUri, expectedKind, expectedThread))
    {
      throw new InvalidDataException($"Rejected snapshot for {logicalUri.Host}.");
    }
  }

  private static bool MatchesLogicalTarget(
      Uri finalUri,
      Uri logicalUri,
      PageKind expectedKind,
      ThreadKey? expectedThread) => expectedKind switch
      {
        PageKind.FavoriteIndex =>
            PaginationFrontier.LogicalPageNumber(finalUri) ==
                PaginationFrontier.LogicalPageNumber(logicalUri) &&
            (SamePathAndQuery(finalUri, logicalUri) ||
             IsFavoriteUri(finalUri) && IsFavoriteUri(logicalUri)),
        PageKind.ThreadDetail => MatchesThreadTarget(finalUri, logicalUri, expectedThread),
        _ => SamePathAndQuery(finalUri, logicalUri)
      };

  private static bool MatchesThreadTarget(
      Uri finalUri,
      Uri logicalUri,
      ThreadKey? expectedThread)
  {
    if (expectedThread is not { } expected)
    {
      return SamePathAndQuery(finalUri, logicalUri);
    }

    if (!Rfbm.Core.Models.ThreadKey.TryParse(
            finalUri.AbsoluteUri,
            expected.SiteFamily.Value,
            out var finalKey))
    {
      return SamePathAndQuery(finalUri, logicalUri);
    }

    return string.Equals(finalKey.ThreadId, expected.ThreadId, StringComparison.OrdinalIgnoreCase) &&
        ThreadLogicalPageNumber(finalUri) == ThreadLogicalPageNumber(logicalUri);
  }

  private static int ThreadLogicalPageNumber(Uri uri)
  {
    var generalPage = PaginationFrontier.LogicalPageNumber(uri);
    if (generalPage != 1)
    {
      return generalPage;
    }

    var fileName = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
    var extension = fileName.IndexOf('.');
    if (extension >= 0)
    {
      fileName = fileName[..extension];
    }
    var parts = fileName.Split('-', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 3 &&
           (parts[0].Equals("thread", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("tid", StringComparison.OrdinalIgnoreCase)) &&
           int.TryParse(
               parts[2],
               System.Globalization.NumberStyles.None,
               System.Globalization.CultureInfo.InvariantCulture,
               out var page)
        ? page
        : generalPage;
  }

  private static bool SamePathAndQuery(Uri left, Uri right) =>
      string.Equals(left.PathAndQuery, right.PathAndQuery, StringComparison.Ordinal);

  private static bool IsFavoriteUri(Uri uri) =>
      uri.PathAndQuery.Contains("favorite", StringComparison.OrdinalIgnoreCase);
}
