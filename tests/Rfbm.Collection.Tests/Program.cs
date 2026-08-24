using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Rfbm.Collection.Acquisition;
using Rfbm.Collection.Adapters;
using Rfbm.Collection.Contracts;
using Rfbm.Collection.Engine;
using Rfbm.Collection.Media;
using Rfbm.Collection.Network;
using Rfbm.Collection.Playwright;
using Rfbm.Data.Storage;
using Rfbm.Tests;

var testRoot = Path.Combine(Path.GetTempPath(), "rfbm-collection-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
  await SuccessfulRunClosesDeclaredPaginationAsync(testRoot);
  await DeclaredPaginationCannotSucceedWithHolesAsync(testRoot);
  await NextOnlyPaginationClosesOnExplicitTerminalAsync(testRoot);
  await RedirectedSecondPageToFirstIsRejectedAsync(testRoot);
  await ProjectionCurrentPageMustMatchRequestedPageAsync(testRoot);
  await MissingTerminalEvidenceCannotSucceedAsync(testRoot);
  await RejectedSnapshotDoesNotAbortIndependentPagesAsync(testRoot);
  await FailedPageProducesHonestPartialResultAsync(testRoot);
  await PartialRunCanResumeToSuccessAsync(testRoot);
  await ResumeRebuildsCountersFromDurableItemsAsync(testRoot);
  await FindResumeCandidateRecoversAbandonedRunAsync(testRoot);
  await AbandonedActiveRunIsRecoveredOnNextProcessSessionAsync(testRoot);
  await ResumeLookupDoesNotInterruptLiveRunAsync(testRoot);
  await SingleActiveRunAndExplicitCancellationAsync(testRoot);
  await CancellingAfterCompletionDoesNotRewriteTerminalStateAsync(testRoot);
  await LoginRequirementEntersWaitingStateAndResumesAsync(testRoot);
  await ObserverCancellationDoesNotCancelRunAsync(testRoot);
  await ThreadConcurrencyIsBoundedAndActuallyUsedAsync(testRoot);
  await FavoritePageConcurrencyIsBoundedAndActuallyUsedAsync(testRoot);
  await SessionEventBufferRetainsCriticalEventsUnderPressureAsync();
  await FavoritePrefetchFailureCancelsAndDrainsStartedNavigationsAsync(testRoot);
  await ThreadAcquisitionExceptionFailsOnlyThatItemAsync(testRoot);
  await DiscuzFavoriteQueryPrettyRedirectIsAcceptedAsync(testRoot);
  await DiscuzThreadQueryPrettyMirrorRedirectIsAcceptedAsync(testRoot);
  await DiscuzThreadPrettyRedirectWithWrongPageIsRejectedAsync(testRoot);
  await CrossThreadPrettyRedirectIsRejectedAsync(testRoot);
  await ThreadReferenceKeyMustMatchSamePathTargetAsync(testRoot);
  await ErrorSnapshotAtExpectedUrlIsRejectedAsync(testRoot);
  await AtomicImageStoreCommitsOnlyImagesAsync(testRoot);
  ImageNetworkPolicyRejectsNonPublicDestinations();
  await RetryableNavigationFallsBackToConfiguredMirrorAsync(testRoot);
  await ConfiguredNetworkRecoveryRunsOnceAfterOriginsFailAsync(testRoot);
  await OptionalNetworkRecoveryFailureDegradesGracefullyAsync(testRoot);
  await MihomoRecoveryUsesOnlyConfiguredControllerAsync();
  SnapshotClassifierRejectsLoginAndChallengePages();
  SnapshotClassifierDoesNotTreatDiscussionTextAsAChallenge();
  PlaywrightRejectsRepositoryAndDefaultEdgeProfiles();
  PlaywrightProfileGuardDoesNotDependOnCurrentDirectory(testRoot);
  await VisibleUserResolutionHonorsPreCancellationAsync(testRoot);
  await PlaywrightAcquiresLocalFixtureWithDedicatedProfileAsync(testRoot);
  DiscuzAdapterBridgesVerifiedHtmlIntoCollectionContracts();
  await FullVerticalSlicePersistsParsedPostAsync(testRoot);
  await VerticalSliceDownloadsImagesAndUpdatesDatabaseAsync(testRoot);
  await ImageTimeoutFailsOnlyTheCurrentWorkItemAsync(testRoot);
  await CancellationDuringImageDownloadLeavesCapturedWorkResumableAsync(testRoot);
  await ResumePreservesFavoriteContextDiscoveredBeforeCrashAsync(testRoot);
  Console.WriteLine("Rfbm.Collection.Tests passed.");
}
finally
{
  SqliteConnection.ClearAllPools();
  Directory.Delete(testRoot, recursive: true);
}

static async Task MihomoRecoveryUsesOnlyConfiguredControllerAsync()
{
  var handler = new RecordingMihomoHandler();
  using var http = new HttpClient(handler)
  {
    BaseAddress = new Uri("http://127.0.0.1:9090")
  };
  var recovery = new MihomoNetworkRecoveryAdapter(http, "controller-secret");

  var result = await recovery.SelectProxyAsync("AUTO", "fixture-node", CancellationToken.None);

  TestAssert.Equal(true, result.Changed);
  TestAssert.Equal(HttpMethod.Put, handler.Request!.Method);
  TestAssert.Equal("/proxies/AUTO", handler.Request.RequestUri!.AbsolutePath);
  TestAssert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
  TestAssert.Equal("controller-secret", handler.Request.Headers.Authorization.Parameter);
  TestAssert.Equal(true, handler.Body!.Contains("fixture-node", StringComparison.Ordinal));
}

static void ImageNetworkPolicyRejectsNonPublicDestinations()
{
  TestAssert.Equal(false, ImageNetworkPolicy.IsPublicAddress(IPAddress.Parse("10.0.0.1")));
  TestAssert.Equal(false, ImageNetworkPolicy.IsPublicAddress(IPAddress.Parse("169.254.169.254")));
  TestAssert.Equal(false, ImageNetworkPolicy.IsPublicAddress(IPAddress.Parse("100.64.0.1")));
  TestAssert.Equal(false, ImageNetworkPolicy.IsPublicAddress(IPAddress.Parse("::1")));
  TestAssert.Equal(false, ImageNetworkPolicy.IsPublicAddress(IPAddress.Parse("fc00::1")));
  TestAssert.Equal(true, ImageNetworkPolicy.IsPublicAddress(IPAddress.Parse("1.1.1.1")));
  TestAssert.Equal(true, ImageNetworkPolicy.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")));
}

static async Task RetryableNavigationFallsBackToConfiguredMirrorAsync(string root)
{
  var databasePath = Path.Combine(root, "mirror-fallback.sqlite3");
  var acquisition = new MirrorFallbackAcquisition();
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(databasePath)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(2, acquisition.Requests.Count);
  TestAssert.Equal("fixture.invalid", acquisition.Requests[0].Host);
  TestAssert.Equal("mirror.fixture.invalid", acquisition.Requests[1].Host);
}

static async Task ConfiguredNetworkRecoveryRunsOnceAfterOriginsFailAsync(string root)
{
  var databasePath = Path.Combine(root, "network-recovery.sqlite3");
  var recovery = new FakeNetworkRecovery();
  var acquisition = new RecoveryAwareAcquisition(recovery);
  var engine = new CollectionEngine(
      acquisition,
      new ScriptedForumAdapter(),
      networkRecovery: recovery);

  await using var session = await engine.OpenAsync(new StartCollection(Plan(databasePath)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(1, recovery.CallCount);
  TestAssert.Equal(3, acquisition.Requests.Count);
}

static async Task OptionalNetworkRecoveryFailureDegradesGracefullyAsync(string root)
{
  var databasePath = Path.Combine(root, "network-recovery-failure.sqlite3");
  var engine = new CollectionEngine(
      new AlwaysUnavailableAcquisition(),
      new ScriptedForumAdapter(),
      networkRecovery: new ThrowingNetworkRecovery());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(databasePath)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(true, result.CanResume);
  TestAssert.Equal(1, result.PagesFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.PaginationIncomplete));
}

static void SnapshotClassifierRejectsLoginAndChallengePages()
{
  TestAssert.Equal(
      SnapshotClassification.LoginRequired,
      PlaywrightSnapshotClassifier.Classify("<form id='loginform'>登录</form>"));
  TestAssert.Equal(
      SnapshotClassification.HumanVerificationRequired,
      PlaywrightSnapshotClassifier.Classify("<title>安全验证</title><div class='captcha'></div>"));
  TestAssert.Equal(
      SnapshotClassification.Content,
      PlaywrightSnapshotClassifier.Classify("<article>ordinary forum post</article>"));
}

static void SnapshotClassifierDoesNotTreatDiscussionTextAsAChallenge()
{
  TestAssert.Equal(
      SnapshotClassification.Content,
      PlaywrightSnapshotClassifier.Classify(
          "<article><h1>如何处理验证码</h1><p>正文讨论 captcha 与 loginform 的实现。</p></article>"));
}

static void PlaywrightRejectsRepositoryAndDefaultEdgeProfiles()
{
  TestAssert.Throws<ArgumentException>(() => _ = new PlaywrightEdgeAcquisitionAdapter(new(
      Path.Combine(Environment.CurrentDirectory, "unsafe-profile"))));

  var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  TestAssert.Throws<ArgumentException>(() => _ = new PlaywrightEdgeAcquisitionAdapter(new(
      Path.Combine(local, "Microsoft", "Edge", "User Data"))));
}

static void PlaywrightProfileGuardDoesNotDependOnCurrentDirectory(string root)
{
  var originalDirectory = Environment.CurrentDirectory;
  var unrelatedDirectory = Path.Combine(root, "unrelated-cwd");
  Directory.CreateDirectory(unrelatedDirectory);
  try
  {
    Environment.CurrentDirectory = unrelatedDirectory;
    TestAssert.Throws<ArgumentException>(() => _ = new PlaywrightEdgeAcquisitionAdapter(new(
        Path.Combine(AppContext.BaseDirectory, "unsafe-profile"))));
  }
  finally
  {
    Environment.CurrentDirectory = originalDirectory;
  }
}

static async Task VisibleUserResolutionHonorsPreCancellationAsync(string root)
{
  await using var adapter = new PlaywrightEdgeAcquisitionAdapter(new(
      ProfileDirectory: Path.Combine(root, "cancelled-visible-profile"),
      Headless: true));
  using var cancellation = new CancellationTokenSource();
  cancellation.Cancel();
  await ThrowsAsync<OperationCanceledException>(() => adapter.ResolveUserActionAsync(
      new PageRequest(
          new CollectionRunId(Guid.NewGuid()),
          PageKind.FavoriteIndex,
          new Uri("https://fixture.invalid/favorites?page=1"),
          null,
          1024 * 1024),
      new PageNeedsUser(
          CollectionErrorCode.AuthenticationRequired,
          new Uri("https://fixture.invalid/favorites?page=1"),
          "fixture"),
      cancellation.Token).AsTask());
}

static async Task PlaywrightAcquiresLocalFixtureWithDedicatedProfileAsync(string root)
{
  var portProbe = new TcpListener(IPAddress.Loopback, 0);
  portProbe.Start();
  var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
  portProbe.Stop();

  using var listener = new HttpListener();
  listener.Prefixes.Add($"http://127.0.0.1:{port}/");
  listener.Start();
  var server = Task.Run(async () =>
  {
    var context = await listener.GetContextAsync();
    var payload = Encoding.UTF8.GetBytes("<!doctype html><title>fixture</title><article>browser fixture</article>");
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.ContentLength64 = payload.Length;
    await context.Response.OutputStream.WriteAsync(payload);
    context.Response.Close();
  });

  var uri = new Uri($"http://127.0.0.1:{port}/");
  await using var adapter = new PlaywrightEdgeAcquisitionAdapter(new(
      ProfileDirectory: Path.Combine(root, "edge-profile"),
      Headless: true));
  var result = await adapter.AcquireAsync(new PageRequest(
      new CollectionRunId(Guid.NewGuid()),
      PageKind.FavoriteIndex,
      uri,
      null,
      1024 * 1024), CancellationToken.None);

  var captured = result as PageCaptured
      ?? throw new InvalidOperationException($"Expected PageCaptured, got {result.GetType().Name}.");
  TestAssert.Equal(uri, captured.Snapshot.FinalUri);
  TestAssert.Equal(true, captured.Snapshot.Document.Contains("browser fixture", StringComparison.Ordinal));
  await server.WaitAsync(TimeSpan.FromSeconds(5));
}

static void DiscuzAdapterBridgesVerifiedHtmlIntoCollectionContracts()
{
  var adapter = new DiscuzForumSourceAdapter();
  var site = new SiteDefinition(
      new SiteFamily("fixture-site"),
      new Uri("https://fixture.invalid"),
      [new Uri("https://mirror.fixture.invalid")]);
  var snapshot = new PageSnapshot(
      new Uri("https://fixture.invalid/favorites?page=1"),
      new Uri("https://fixture.invalid/favorites?page=1"),
      "text/html",
      "<main id='ct'><ul id='favorite_ul'><li id='favorite_li_1'><a href='/thread-42-1-1.html'>收藏帖</a><span>2026-08-24 09:00</span></li></ul></main><div class='pg'><strong>1</strong><a href='/favorites?page=2'>2</a><a class='nxt' href='/favorites?page=2'>下一页</a></div>",
      DateTimeOffset.UtcNow);

  var projection = adapter.ParseFavoritePage(snapshot, site);

  TestAssert.Equal(2, projection.DeclaredLastPage);
  TestAssert.Equal("https://fixture.invalid/favorites?page=2", projection.NextUri!.AbsoluteUri);
  TestAssert.Equal(false, projection.HasExplicitTerminalEvidence);
  TestAssert.Equal(1, projection.Threads.Length);
  TestAssert.Equal("42", projection.Threads[0].Key.ThreadId);
  TestAssert.Equal("收藏帖", projection.Threads[0].Title);
  TestAssert.Equal("2026-08-24 09:00", projection.Threads[0].RawFavoriteTime);
  TestAssert.Equal(false, projection.Threads[0].FavoriteTime is null);
  TestAssert.Equal(snapshot.FinalUri, projection.Threads[0].SourcePageUri);
}

static async Task FullVerticalSlicePersistsParsedPostAsync(string root)
{
  var databasePath = Path.Combine(root, "vertical.sqlite3");
  var plan = new CollectionPlan(
      databasePath,
      new SiteDefinition(
          new SiteFamily("fixture-site"),
          new Uri("https://fixture.invalid"),
          ImmutableArray<Uri>.Empty),
      new Uri("https://fixture.invalid/favorites?page=1"),
      DownloadPostImages: false);
  var engine = new CollectionEngine(new HtmlAcquisition(), new DiscuzForumSourceAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;
  TestAssert.Equal(RunState.Succeeded, result.State);

  var database = new BookmarkDatabase(databasePath);
  await database.InitializeAsync();
  var saved = TestAssert.NotNull(await new PostRepository(database).GetAsync(
      new PostKey("fixture-site", "77")));
  TestAssert.Equal("纵向切片收藏", saved.Title);
  TestAssert.Equal("SAVE-77", saved.ExtractPassword);
  TestAssert.Equal("2026-08-24 12:00", saved.FavoriteTimeRaw);
  TestAssert.Equal(false, saved.FavoriteTimeUtc is null);
  TestAssert.Equal("https://fixture.invalid/favorites?page=1", saved.FavoriteUrl);
  TestAssert.Equal(1, saved.Images.Count);
  TestAssert.Equal(1, saved.ResourceLinks.Count);
  var snapshot = TestAssert.NotNull(await new PostRepository(database).GetCaptureSnapshotAsync(
      session.Id.ToString(),
      "fixture-site:77"));
  TestAssert.Equal("committed", snapshot.WorkState);
  TestAssert.Equal(false, snapshot.MediaPending);
  TestAssert.Equal("ThreadDetail", snapshot.Kind);
  TestAssert.Equal("https://fixture.invalid/thread-77-1-1.html", snapshot.RequestedUrl);
  TestAssert.Equal(64, snapshot.Sha256.Length);
}

static async Task VerticalSliceDownloadsImagesAndUpdatesDatabaseAsync(string root)
{
  var databasePath = Path.Combine(root, "vertical-images.sqlite3");
  var mediaDirectory = Path.Combine(root, "vertical-media");
  var plan = new CollectionPlan(
      databasePath,
      new SiteDefinition(
          new SiteFamily("fixture-site"),
          new Uri("https://fixture.invalid"),
          ImmutableArray<Uri>.Empty),
      new Uri("https://fixture.invalid/favorites?page=1"),
      MediaDirectory: mediaDirectory);
  using var imageClient = new HttpClient(new StaticImageHandler());
  var engine = new CollectionEngine(
      new HtmlAcquisition(),
      new DiscuzForumSourceAdapter(),
      imageClient);

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;
  TestAssert.Equal(RunState.Succeeded, result.State);

  var repository = new PostRepository(new BookmarkDatabase(databasePath));
  var saved = TestAssert.NotNull(await repository.GetAsync(new PostKey("fixture-site", "77")));
  TestAssert.Equal(1, saved.Images.Count);
  TestAssert.Equal("downloaded", saved.Images[0].DownloadStatus);
  var relativePath = TestAssert.NotNull(saved.Images[0].LocalPath);
  TestAssert.Equal(true, File.Exists(Path.Combine(mediaDirectory, relativePath)));
  var snapshot = TestAssert.NotNull(await repository.GetCaptureSnapshotAsync(
      session.Id.ToString(),
      "fixture-site:77"));
  TestAssert.Equal("committed", snapshot.WorkState);
  TestAssert.Equal(false, snapshot.MediaPending);
  TestAssert.Equal("https://fixture.invalid/thread-77-1-1.html", snapshot.FinalUrl);
}

static async Task ImageTimeoutFailsOnlyTheCurrentWorkItemAsync(string root)
{
  var databasePath = Path.Combine(root, "timeout-image.sqlite3");
  var plan = new CollectionPlan(
      databasePath,
      new SiteDefinition(
          new SiteFamily("fixture-site"),
          new Uri("https://fixture.invalid"),
          ImmutableArray<Uri>.Empty),
      new Uri("https://fixture.invalid/favorites?page=1"),
      MediaDirectory: Path.Combine(root, "timeout-image-media"));
  using var imageClient = new HttpClient(new TimeoutImageHandler());
  var engine = new CollectionEngine(
      new HtmlAcquisition(),
      new DiscuzForumSourceAdapter(),
      imageClient);

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;
  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(true, result.CanResume);
  TestAssert.Equal(1, result.ThreadsFailed);

  var snapshot = TestAssert.NotNull(await new PostRepository(new BookmarkDatabase(databasePath))
      .GetCaptureSnapshotAsync(session.Id.ToString(), "fixture-site:77"));
  TestAssert.Equal("failed", snapshot.WorkState);
  TestAssert.Equal(true, snapshot.MediaPending);
}

static async Task CancellationDuringImageDownloadLeavesCapturedWorkResumableAsync(string root)
{
  var databasePath = Path.Combine(root, "cancelled-image.sqlite3");
  var mediaDirectory = Path.Combine(root, "cancelled-image-media");
  var handler = new BlockingImageHandler();
  using var imageClient = new HttpClient(handler);
  var plan = new CollectionPlan(
      databasePath,
      new SiteDefinition(
          new SiteFamily("fixture-site"),
          new Uri("https://fixture.invalid"),
          ImmutableArray<Uri>.Empty),
      new Uri("https://fixture.invalid/favorites?page=1"),
      MediaDirectory: mediaDirectory);
  var engine = new CollectionEngine(
      new HtmlAcquisition(),
      new DiscuzForumSourceAdapter(),
      imageClient);

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
  await session.RequestCancelAsync();
  TestAssert.Equal(RunState.Cancelled, (await session.Completion).State);

  var repository = new PostRepository(new BookmarkDatabase(databasePath));
  var snapshot = TestAssert.NotNull(await repository.GetCaptureSnapshotAsync(
      session.Id.ToString(),
      "fixture-site:77"));
  TestAssert.Equal("captured", snapshot.WorkState);
  TestAssert.Equal(true, snapshot.MediaPending);
  TestAssert.NotNull(await engine.FindResumeCandidateAsync(databasePath));
}

static async Task ResumePreservesFavoriteContextDiscoveredBeforeCrashAsync(string root)
{
  var databasePath = Path.Combine(root, "resume-favorite-context.sqlite3");
  var plan = new CollectionPlan(
      databasePath,
      new SiteDefinition(
          new SiteFamily("fixture-site"),
          new Uri("https://fixture.invalid"),
          ImmutableArray<Uri>.Empty),
      new Uri("https://fixture.invalid/favorites?page=1"),
      DownloadPostImages: false);
  var blocking = new BlockingThreadHtmlAcquisition();
  var firstEngine = new CollectionEngine(blocking, new DiscuzForumSourceAdapter());
  await using var first = await firstEngine.OpenAsync(new StartCollection(plan));
  await blocking.ThreadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
  await first.RequestCancelAsync();
  TestAssert.Equal(RunState.Cancelled, (await first.Completion).State);

  var resumedEngine = new CollectionEngine(new HtmlAcquisition(), new DiscuzForumSourceAdapter());
  await using var resumed = await resumedEngine.OpenAsync(new ResumeCollection(databasePath, first.Id));
  TestAssert.Equal(RunState.Succeeded, (await resumed.Completion).State);

  var saved = TestAssert.NotNull(await new PostRepository(new BookmarkDatabase(databasePath)).GetAsync(
      new PostKey("fixture-site", "77")));
  TestAssert.Equal("2026-08-24 12:00", saved.FavoriteTimeRaw);
  TestAssert.Equal("https://fixture.invalid/favorites?page=1", saved.FavoriteUrl);
}

static CollectionPlan Plan(string databasePath) => new(
    DatabasePath: databasePath,
    Site: new SiteDefinition(
        new SiteFamily("fixture-site"),
        new Uri("https://fixture.invalid"),
        [new Uri("https://mirror.fixture.invalid")]),
    FavoritesUrl: new Uri("https://fixture.invalid/favorites?page=1"),
    FavoritePageConcurrency: 2,
    ThreadConcurrency: 2,
    MaxSnapshotBytes: 1024 * 1024);

static async Task SuccessfulRunClosesDeclaredPaginationAsync(string root)
{
  var db = Path.Combine(root, "success.sqlite3");
  var adapter = ScriptedAcquisition.SuccessfulThreePageRun();
  var engine = new CollectionEngine(adapter, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var events = new List<CollectionEvent>();
  await foreach (var item in session.Events())
  {
    events.Add(item);
  }

  var result = await session.Completion;
  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(3, result.PagesDiscovered);
  TestAssert.Equal(3, result.PagesCommitted);
  TestAssert.Equal(2, result.ThreadsDiscovered);
  TestAssert.Equal(2, result.ThreadsCommitted);
  TestAssert.Equal(false, result.CanResume);
  TestAssert.Equal(true, events.Count >= 3);
  TestAssert.Equal(true, events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
  TestAssert.Equal(1, adapter.Requests.Count(uri => uri.AbsolutePath == "/thread/42"));
}

static async Task DeclaredPaginationCannotSucceedWithHolesAsync(string root)
{
  var db = Path.Combine(root, "declared-closure.sqlite3");
  var acquisition = ScriptedAcquisition.ThreeFavoritePagesWithoutThreads();
  var engine = new CollectionEngine(acquisition, new IncompleteEvidenceForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(3, result.PagesCommitted);
  TestAssert.Equal(3, acquisition.Requests.Count(uri => uri.AbsolutePath == "/favorites"));
}

static async Task NextOnlyPaginationClosesOnExplicitTerminalAsync(string root)
{
  var db = Path.Combine(root, "next-only.sqlite3");
  var acquisition = ScriptedAcquisition.NextOnlyTwoPageRun();
  var engine = new CollectionEngine(acquisition, new NextOnlyForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(2, result.PagesCommitted);
  TestAssert.Equal(2, acquisition.Requests.Count(uri => uri.AbsolutePath == "/favorites"));
  TestAssert.Equal(true, acquisition.Requests.Any(uri => uri.Query.Contains("page=2", StringComparison.Ordinal)));
}

static async Task RedirectedSecondPageToFirstIsRejectedAsync(string root)
{
  var db = Path.Combine(root, "redirected-page.sqlite3");
  var acquisition = ScriptedAcquisition.SecondPageRedirectsToFirst();
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(1, result.PagesCommitted);
  TestAssert.Equal(1, result.PagesFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.SnapshotRejected));
}

static async Task ProjectionCurrentPageMustMatchRequestedPageAsync(string root)
{
  var db = Path.Combine(root, "wrong-projection-page.sqlite3");
  var acquisition = ScriptedAcquisition.SecondPageClaimsFirst();
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(1, result.PagesCommitted);
  TestAssert.Equal(1, result.PagesFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.PaginationIncomplete));
}

static async Task MissingTerminalEvidenceCannotSucceedAsync(string root)
{
  var db = Path.Combine(root, "missing-terminal.sqlite3");
  var acquisition = ScriptedAcquisition.NoTerminalEvidence();
  var engine = new CollectionEngine(acquisition, new NextOnlyForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(true, result.CanResume);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.PaginationIncomplete));
}

static async Task RejectedSnapshotDoesNotAbortIndependentPagesAsync(string root)
{
  var db = Path.Combine(root, "bad-snapshot.sqlite3");
  var acquisition = ScriptedAcquisition.WithRejectedSecondPage();
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(2, result.PagesCommitted);
  TestAssert.Equal(1, result.PagesFailed);
  TestAssert.Equal(true, acquisition.Requests.Any(uri => uri.Query.Contains("page=3", StringComparison.Ordinal)));
}

static async Task FailedPageProducesHonestPartialResultAsync(string root)
{
  var db = Path.Combine(root, "partial.sqlite3");
  var adapter = ScriptedAcquisition.WithMissingSecondPage();
  var engine = new CollectionEngine(adapter, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(true, result.CanResume);
  TestAssert.Equal(1, result.PagesFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.PaginationIncomplete));

  await using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
  {
    await connection.OpenAsync();
    await using var frontier = connection.CreateCommand();
    frontier.CommandText = "SELECT state FROM collection_frontier WHERE uri LIKE '%page=2';";
    TestAssert.Equal("failed", (string?)await frontier.ExecuteScalarAsync());
    await using var work = connection.CreateCommand();
    work.CommandText = "SELECT state FROM collection_work_items WHERE logical_key = 'fixture-site:42';";
    TestAssert.Equal("committed", (string?)await work.ExecuteScalarAsync());
  }

  var resume = await engine.FindResumeCandidateAsync(db);
  TestAssert.NotNull(resume);
  TestAssert.Equal(session.Id, resume!.Id);
}

static async Task PartialRunCanResumeToSuccessAsync(string root)
{
  var db = Path.Combine(root, "resume.sqlite3");
  var firstEngine = new CollectionEngine(
      ScriptedAcquisition.WithMissingSecondPage(),
      new ScriptedForumAdapter());
  await using var first = await firstEngine.OpenAsync(new StartCollection(Plan(db)));
  var partial = await first.Completion;
  TestAssert.Equal(RunState.PartiallySucceeded, partial.State);

  var resumedAcquisition = ScriptedAcquisition.SuccessfulTwoPageRun();
  var resumedEngine = new CollectionEngine(resumedAcquisition, new ScriptedForumAdapter());
  await using var resumed = await resumedEngine.OpenAsync(new ResumeCollection(db, first.Id));
  var completed = await resumed.Completion;

  TestAssert.Equal(first.Id, resumed.Id);
  TestAssert.Equal(RunState.Succeeded, completed.State);
  TestAssert.Equal(2, completed.PagesCommitted);
  TestAssert.Equal(1, completed.ThreadsCommitted);
  TestAssert.Equal(1, resumedAcquisition.Requests.Count);
  TestAssert.Equal("page=2", resumedAcquisition.Requests[0].Query.TrimStart('?'));
  TestAssert.Equal<RunSummary?>(null, await resumedEngine.FindResumeCandidateAsync(db));
}

static async Task ResumeRebuildsCountersFromDurableItemsAsync(string root)
{
  var db = Path.Combine(root, "resume-derived-counters.sqlite3");
  var firstEngine = new CollectionEngine(
      ScriptedAcquisition.SuccessfulTwoPageRun(),
      new ScriptedForumAdapter());
  await using var first = await firstEngine.OpenAsync(new StartCollection(Plan(db)));
  TestAssert.Equal(RunState.Succeeded, (await first.Completion).State);

  await using (var connection = new BookmarkDatabase(db).CreateConnection())
  {
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
            UPDATE collection_runs
            SET state = 'Interrupted', can_resume = 1,
                pages_discovered = 0, pages_committed = 0, pages_failed = 0,
                threads_discovered = 0, threads_committed = 0, threads_failed = 0
            WHERE id = $id;

            UPDATE collection_frontier
            SET state = 'committed'
            WHERE run_id = $id;
            """;
    command.Parameters.AddWithValue("$id", first.Id.ToString());
    await command.ExecuteNonQueryAsync();
  }

  var resumedAcquisition = ScriptedAcquisition.SuccessfulTwoPageRun();
  var resumedEngine = new CollectionEngine(
      resumedAcquisition,
      new ScriptedForumAdapter());
  await using var resumed = await resumedEngine.OpenAsync(new ResumeCollection(db, first.Id));
  var result = await resumed.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(2, result.PagesDiscovered);
  TestAssert.Equal(2, result.PagesCommitted);
  TestAssert.Equal(1, result.ThreadsDiscovered);
  TestAssert.Equal(1, result.ThreadsCommitted);
  TestAssert.Equal(1, resumedAcquisition.Requests.Count);
  TestAssert.Equal("page=2", resumedAcquisition.Requests[0].Query.TrimStart('?'));
}

static async Task AbandonedActiveRunIsRecoveredOnNextProcessSessionAsync(string root)
{
  var db = Path.Combine(root, "abandoned.sqlite3");
  var database = new BookmarkDatabase(db);
  await database.InitializeAsync();
  var abandonedId = Guid.NewGuid().ToString("N");
  await using (var connection = database.CreateConnection())
  {
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
            INSERT INTO collection_runs (id, state, plan_json, started_at)
            VALUES ($id, 'Collecting', '{}', $started);
            """;
    command.Parameters.AddWithValue("$id", abandonedId);
    command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
    await command.ExecuteNonQueryAsync();
  }

  var engine = new CollectionEngine(ScriptedAcquisition.DelayedSuccess(), new ScriptedForumAdapter());
  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  TestAssert.Equal(RunState.Succeeded, (await session.Completion).State);

  await using var verify = database.CreateConnection();
  await verify.OpenAsync();
  await using var state = verify.CreateCommand();
  state.CommandText = "SELECT state || ':' || can_resume FROM collection_runs WHERE id = $id;";
  state.Parameters.AddWithValue("$id", abandonedId);
  TestAssert.Equal("Interrupted:1", (string?)await state.ExecuteScalarAsync());
}

static async Task FindResumeCandidateRecoversAbandonedRunAsync(string root)
{
  var db = Path.Combine(root, "resume-lookup-recovery.sqlite3");
  var database = new BookmarkDatabase(db);
  await database.InitializeAsync();
  var abandonedId = Guid.NewGuid().ToString("N");
  await using (var connection = database.CreateConnection())
  {
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
            INSERT INTO collection_runs (id, state, plan_json, started_at)
            VALUES ($id, 'Collecting', '{}', $started);
            """;
    command.Parameters.AddWithValue("$id", abandonedId);
    command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
    await command.ExecuteNonQueryAsync();
  }

  var engine = new CollectionEngine(ScriptedAcquisition.DelayedSuccess(), new ScriptedForumAdapter());
  var candidate = TestAssert.NotNull(await engine.FindResumeCandidateAsync(db));

  TestAssert.Equal(new CollectionRunId(Guid.ParseExact(abandonedId, "N")), candidate.Id);
  TestAssert.Equal(RunState.Interrupted, candidate.State);
  TestAssert.Equal(true, candidate.CanResume);
}

static async Task ResumeLookupDoesNotInterruptLiveRunAsync(string root)
{
  var db = Path.Combine(root, "live-resume-lookup.sqlite3");
  var acquisition = ScriptedAcquisition.Blocking();
  var runningEngine = new CollectionEngine(acquisition, new ScriptedForumAdapter());
  await using var running = await runningEngine.OpenAsync(new StartCollection(Plan(db)));
  await acquisition.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

  var observerEngine = new CollectionEngine(ScriptedAcquisition.DelayedSuccess(), new ScriptedForumAdapter());
  TestAssert.Equal<RunSummary?>(null, await observerEngine.FindResumeCandidateAsync(db));

  await using (var connection = new BookmarkDatabase(db).CreateConnection())
  {
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT state FROM collection_runs WHERE id = $id;";
    command.Parameters.AddWithValue("$id", running.Id.ToString());
    TestAssert.Equal(RunState.Discovering.ToString(), (string?)await command.ExecuteScalarAsync());
  }

  await running.RequestCancelAsync();
  TestAssert.Equal(RunState.Cancelled, (await running.Completion).State);
}

static async Task SingleActiveRunAndExplicitCancellationAsync(string root)
{
  var db = Path.Combine(root, "cancel.sqlite3");
  var adapter = ScriptedAcquisition.Blocking();
  var engine = new CollectionEngine(adapter, new ScriptedForumAdapter());
  await using var first = await engine.OpenAsync(new StartCollection(Plan(db)));

  await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
  await ThrowsAsync<CollectionOpenException>(
      () => engine.OpenAsync(new StartCollection(Plan(db))).AsTask(),
      ex => TestAssert.Equal(CollectionErrorCode.EngineBusy, ex.Code));

  await first.RequestCancelAsync();
  var result = await first.Completion;
  TestAssert.Equal(RunState.Cancelled, result.State);
  TestAssert.Equal(true, result.CanResume);
}

static async Task CancellingAfterCompletionDoesNotRewriteTerminalStateAsync(string root)
{
  var db = Path.Combine(root, "terminal-cancel.sqlite3");
  var engine = new CollectionEngine(ScriptedAcquisition.DelayedSuccess(), new ScriptedForumAdapter());
  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  TestAssert.Equal(RunState.Succeeded, (await session.Completion).State);

  await session.RequestCancelAsync();
  await using var connection = new BookmarkDatabase(db).CreateConnection();
  await connection.OpenAsync();
  await using var command = connection.CreateCommand();
  command.CommandText = "SELECT state FROM collection_runs WHERE id = $id;";
  command.Parameters.AddWithValue("$id", session.Id.ToString());
  TestAssert.Equal(RunState.Succeeded.ToString(), (string?)await command.ExecuteScalarAsync());
}

static async Task LoginRequirementEntersWaitingStateAndResumesAsync(string root)
{
  var db = Path.Combine(root, "interactive.sqlite3");
  var acquisition = new InteractiveAcquisition();
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());
  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var events = new List<CollectionEvent>();
  await foreach (var item in session.Events())
  {
    events.Add(item);
  }

  var result = await session.Completion;
  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(1, acquisition.UserActionCount);
  TestAssert.Equal(true, events.OfType<StateChanged>().Any(item => item.State == RunState.WaitingForUser));
}

static async Task ObserverCancellationDoesNotCancelRunAsync(string root)
{
  var db = Path.Combine(root, "observer.sqlite3");
  var adapter = ScriptedAcquisition.DelayedSuccess();
  var engine = new CollectionEngine(adapter, new ScriptedForumAdapter());
  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));

  using var observer = new CancellationTokenSource();
  var watch = Task.Run(async () =>
  {
    await foreach (var _ in session.Events(observer.Token))
    {
      observer.Cancel();
    }
  });
  await ThrowsAsync<OperationCanceledException>(() => watch);

  var result = await session.Completion;
  TestAssert.Equal(RunState.Succeeded, result.State);
}

static async Task ThreadConcurrencyIsBoundedAndActuallyUsedAsync(string root)
{
  var db = Path.Combine(root, "concurrency.sqlite3");
  var acquisition = new ConcurrentThreadAcquisition();
  var plan = Plan(db) with { ThreadConcurrency = 3 };
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(3, result.ThreadsCommitted);
  TestAssert.Equal(true, acquisition.MaximumConcurrentThreads >= 2);
  TestAssert.Equal(true, acquisition.MaximumConcurrentThreads <= 3);
}

static async Task FavoritePageConcurrencyIsBoundedAndActuallyUsedAsync(string root)
{
  var db = Path.Combine(root, "favorite-concurrency.sqlite3");
  var acquisition = new ConcurrentFavoritePageAcquisition();
  var plan = Plan(db) with { FavoritePageConcurrency = 3 };
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(4, result.PagesCommitted);
  TestAssert.Equal(true, acquisition.MaximumConcurrentFavoritePages >= 2);
  TestAssert.Equal(true, acquisition.MaximumConcurrentFavoritePages <= 3);
}

static async Task FavoritePrefetchFailureCancelsAndDrainsStartedNavigationsAsync(string root)
{
  var db = Path.Combine(root, "favorite-prefetch-drain.sqlite3");
  var acquisition = new ExplodingFavoritePrefetchAcquisition();
  var plan = Plan(db) with { FavoritePageConcurrency = 2 };
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;
  var activeAtCompletion = acquisition.ActiveNavigations;
  var engineCancelledAtCompletion = acquisition.EngineCancellationObserved;
  acquisition.ReleaseForCleanup();
  await acquisition.AllNavigationsStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

  TestAssert.Equal(RunState.Failed, result.State);
  TestAssert.Equal(0, activeAtCompletion);
  TestAssert.Equal(true, engineCancelledAtCompletion);
}

static async Task ThreadAcquisitionExceptionFailsOnlyThatItemAsync(string root)
{
  var db = Path.Combine(root, "thread-acquisition-isolation.sqlite3");
  var acquisition = new OneExplodingThreadAcquisition();
  var plan = Plan(db) with { ThreadConcurrency = 2 };
  var engine = new CollectionEngine(acquisition, new ScriptedForumAdapter());

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(1, result.ThreadsCommitted);
  TestAssert.Equal(1, result.ThreadsFailed);
  TestAssert.Equal(true, result.Issues.Any(issue =>
      issue.Code == CollectionErrorCode.NavigationFailed &&
      issue.Stage == CollectionStage.Acquire &&
      issue.Thread?.ThreadId == "1"));
}

static async Task DiscuzFavoriteQueryPrettyRedirectIsAcceptedAsync(string root)
{
  var db = Path.Combine(root, "favorite-query-pretty.sqlite3");
  var plan = Plan(db) with
  {
    FavoritesUrl = new Uri(
        "https://fixture.invalid/home.php?mod=space&do=favorite&type=all&page=1")
  };
  var acquisition = new DiscuzSemanticAcquisition(
      favoriteFinalUri: new Uri(
          "https://mirror.fixture.invalid/space-user-do-favorite-page-1.html"));
  var engine = new CollectionEngine(acquisition, new DiscuzSemanticForumAdapter(includeThread: false));

  await using var session = await engine.OpenAsync(new StartCollection(plan));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(1, result.PagesCommitted);
}

static async Task DiscuzThreadQueryPrettyMirrorRedirectIsAcceptedAsync(string root)
{
  var db = Path.Combine(root, "thread-query-pretty.sqlite3");
  var acquisition = new DiscuzSemanticAcquisition(
      threadFinalUri: new Uri("https://mirror.fixture.invalid/thread-42-1-1.html"));
  var engine = new CollectionEngine(acquisition, new DiscuzSemanticForumAdapter(includeThread: true));

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.Succeeded, result.State);
  TestAssert.Equal(1, result.ThreadsCommitted);
}

static async Task CrossThreadPrettyRedirectIsRejectedAsync(string root)
{
  var db = Path.Combine(root, "cross-thread-redirect.sqlite3");
  var acquisition = new DiscuzSemanticAcquisition(
      threadFinalUri: new Uri("https://mirror.fixture.invalid/thread-43-1-1.html"));
  var engine = new CollectionEngine(acquisition, new DiscuzSemanticForumAdapter(includeThread: true));

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(0, result.ThreadsCommitted);
  TestAssert.Equal(1, result.ThreadsFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.SnapshotRejected));
}

static async Task DiscuzThreadPrettyRedirectWithWrongPageIsRejectedAsync(string root)
{
  var db = Path.Combine(root, "thread-wrong-page-redirect.sqlite3");
  var acquisition = new DiscuzSemanticAcquisition(
      threadFinalUri: new Uri("https://mirror.fixture.invalid/thread-42-2-1.html"));
  var engine = new CollectionEngine(acquisition, new DiscuzSemanticForumAdapter(includeThread: true));

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(0, result.ThreadsCommitted);
  TestAssert.Equal(1, result.ThreadsFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.SnapshotRejected));
}

static async Task ThreadReferenceKeyMustMatchSamePathTargetAsync(string root)
{
  var db = Path.Combine(root, "thread-reference-key-mismatch.sqlite3");
  var engine = new CollectionEngine(
      new DiscuzSemanticAcquisition(),
      new DiscuzSemanticForumAdapter(
          includeThread: true,
          threadUri: new Uri("https://fixture.invalid/forum.php?mod=viewthread&tid=43")));

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(0, result.ThreadsCommitted);
  TestAssert.Equal(1, result.ThreadsFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.SnapshotRejected));
}

static async Task ErrorSnapshotAtExpectedUrlIsRejectedAsync(string root)
{
  var db = Path.Combine(root, "error-snapshot.sqlite3");
  var acquisition = new DiscuzSemanticAcquisition(errorFavoritePage: true);
  var engine = new CollectionEngine(acquisition, new DiscuzSemanticForumAdapter(includeThread: false));

  await using var session = await engine.OpenAsync(new StartCollection(Plan(db)));
  var result = await session.Completion;

  TestAssert.Equal(RunState.PartiallySucceeded, result.State);
  TestAssert.Equal(0, result.PagesCommitted);
  TestAssert.Equal(1, result.PagesFailed);
  TestAssert.Equal(true, result.Issues.Any(issue => issue.Code == CollectionErrorCode.SnapshotRejected));
}

static async Task SessionEventBufferRetainsCriticalEventsUnderPressureAsync()
{
  var runId = new CollectionRunId(Guid.NewGuid());
  await using var session = new CollectionSession(runId, _ => ValueTask.CompletedTask);
  var criticalEventCount = CollectionSession.EventCapacity * 2;
  var publishTask = Task.Run(() =>
  {
    for (var index = 0; index < criticalEventCount; index++)
    {
      session.Publish(new ProgressChanged(
          runId,
          0,
          DateTimeOffset.UtcNow,
          index,
          index,
          0,
          index,
          index,
          0));
      session.Publish(new StateChanged(
          runId,
          0,
          DateTimeOffset.UtcNow,
          RunState.Discovering,
          index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      session.Publish(new ItemFailed(
          runId,
          0,
          DateTimeOffset.UtcNow,
          new Uri($"https://fixture.invalid/thread-{index}"),
          new ThreadKey(new SiteFamily("fixture"), index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
          CollectionIssue.Resumable(
              CollectionErrorCode.NavigationFailed,
              CollectionStage.Acquire,
              $"failure-{index}")));
    }
  });
  await publishTask.WaitAsync(TimeSpan.FromSeconds(5));
  session.Publish(new StateChanged(
      runId,
      0,
      DateTimeOffset.UtcNow,
      RunState.Succeeded,
      "complete"));
  session.CompleteEvents();

  var events = new List<CollectionEvent>();
  await foreach (var item in session.Events())
  {
    events.Add(item);
  }

  TestAssert.Equal(criticalEventCount + 1, events.OfType<StateChanged>().Count());
  TestAssert.Equal(criticalEventCount, events.OfType<ItemFailed>().Count());
  TestAssert.Equal(true, events.OfType<ProgressChanged>().Count() <= CollectionSession.EventCapacity);
  TestAssert.Equal(RunState.Succeeded, ((StateChanged)events[^1]).State);
  TestAssert.Equal(true, events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
}

static async Task AtomicImageStoreCommitsOnlyImagesAsync(string root)
{
  var mediaRoot = Path.Combine(root, "media");
  using var http = new HttpClient(new StaticImageHandler());
  var store = new AtomicImageStore(mediaRoot, http, maxBytes: 1024);

  var saved = await store.SaveAsync(
      new Uri("https://fixture.invalid/image.png"),
      new ThreadKey(new SiteFamily("fixture-site"), "42"),
      position: 1,
      captureScope: "generation-1",
      CancellationToken.None);
  TestAssert.Equal(true, File.Exists(saved.FullPath));
  TestAssert.Equal("image/png", saved.MediaType);

  var newer = await store.SaveAsync(
      new Uri("https://fixture.invalid/image.png"),
      new ThreadKey(new SiteFamily("fixture-site"), "42"),
      position: 1,
      captureScope: "generation-2",
      CancellationToken.None);
  TestAssert.Equal(false, string.Equals(saved.FullPath, newer.FullPath, StringComparison.OrdinalIgnoreCase));
  TestAssert.Equal(true, File.Exists(saved.FullPath));
  TestAssert.Equal(true, File.Exists(newer.FullPath));

  await ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
      new Uri("https://fixture.invalid/file.zip"),
      new ThreadKey(new SiteFamily("fixture-site"), "42"),
      position: 2,
      captureScope: "generation-2",
      CancellationToken.None));
  await ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
      new Uri("http://127.0.0.1/private.png"),
      new ThreadKey(new SiteFamily("fixture-site"), "42"),
      position: 2,
      captureScope: "generation-2",
      CancellationToken.None));
  TestAssert.Equal(2, Directory.GetFiles(mediaRoot, "*", SearchOption.AllDirectories).Length);
}

static async Task ThrowsAsync<TException>(Func<Task> action, Action<TException>? inspect = null)
    where TException : Exception
{
  try
  {
    await action();
  }
  catch (TException ex)
  {
    inspect?.Invoke(ex);
    return;
  }

  throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class ScriptedAcquisition : IPageAcquisitionAdapter
{
  private readonly Dictionary<string, Func<CancellationToken, Task<PageAcquisitionResult>>> _responses;

  private ScriptedAcquisition(Dictionary<string, Func<CancellationToken, Task<PageAcquisitionResult>>> responses)
  {
    _responses = responses;
  }

  public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  public List<Uri> Requests { get; } = [];

  public async ValueTask<PageAcquisitionResult> AcquireAsync(PageRequest request, CancellationToken cancellationToken)
  {
    Requests.Add(request.Uri);
    Started.TrySetResult();
    if (_responses.TryGetValue(request.Uri.AbsolutePath + request.Uri.Query, out var response) ||
        _responses.TryGetValue(request.Uri.AbsolutePath, out response))
    {
      var result = await response(cancellationToken);
      if (result is PageCaptured captured)
      {
        var finalUri = captured.Snapshot.FinalUri.AbsolutePath == "/" &&
            captured.Snapshot.FinalUri.Host.Equals(request.Uri.Host, StringComparison.OrdinalIgnoreCase)
            ? request.Uri
            : captured.Snapshot.FinalUri;
        var normalized = captured.Snapshot with
        {
          RequestedUri = request.Uri,
          FinalUri = finalUri,
          ClaimedKind = request.Kind
        };
        return new PageCaptured(normalized with { Sha256 = normalized.ComputedSha256 });
      }
      return result;
    }

    return new PageUnavailable(CollectionIssue.Permanent(
        CollectionErrorCode.NavigationFailed,
        CollectionStage.Acquire,
        "fixture page is unavailable",
        request.Uri));
  }

  public static ScriptedAcquisition SuccessfulThreePageRun() => new(new()
  {
    ["/favorites?page=1"] = Capture("favorites:1:last=3:threads=42"),
    ["/favorites?page=2"] = Capture("favorites:2:last=3:threads=42,43"),
    ["/favorites?page=3"] = Capture("favorites:3:last=3:threads="),
    ["/thread/42"] = Capture("thread:42"),
    ["/thread/43"] = Capture("thread:43")
  });

  public static ScriptedAcquisition ThreeFavoritePagesWithoutThreads() => new(new()
  {
    ["/favorites?page=1"] = Capture("favorites:1:last=3:threads="),
    ["/favorites?page=2"] = Capture("favorites:2:last=3:threads="),
    ["/favorites?page=3"] = Capture("favorites:3:last=3:threads=")
  });

  public static ScriptedAcquisition WithRejectedSecondPage() => new(new()
  {
    ["/favorites?page=1"] = Capture("favorites:1:last=3:threads="),
    ["/favorites?page=2"] = _ => Task.FromResult<PageAcquisitionResult>(new PageCaptured(new PageSnapshot(
        new Uri("https://fixture.invalid/favorites?page=2"),
        new Uri("https://untrusted.invalid/favorites?page=2"),
        "text/html",
        "favorites:2:last=3:threads=",
        DateTimeOffset.UtcNow))),
    ["/favorites?page=3"] = Capture("favorites:3:last=3:threads=")
  });

  public static ScriptedAcquisition WithMissingSecondPage() => new(new()
  {
    ["/favorites?page=1"] = Capture("favorites:1:last=2:threads=42"),
    ["/favorites?page=2"] = _ => Task.FromResult<PageAcquisitionResult>(new PageUnavailable(
        CollectionIssue.Permanent(CollectionErrorCode.NavigationFailed, CollectionStage.Acquire, "missing"))),
    ["/thread/42"] = Capture("thread:42")
  });

  public static ScriptedAcquisition SuccessfulTwoPageRun() => new(new()
  {
    ["/favorites?page=1"] = Capture("favorites:1:last=2:threads=42"),
    ["/favorites?page=2"] = Capture("favorites:2:last=2:threads="),
    ["/thread/42"] = Capture("thread:42")
  });

  public static ScriptedAcquisition NextOnlyTwoPageRun() => new(new()
  {
    ["/favorites?page=1"] = Capture("next-only:1:next=2"),
    ["/favorites?page=2"] = Capture("next-only:2:next=")
  });

  public static ScriptedAcquisition NoTerminalEvidence() => new(new()
  {
    ["/favorites?page=1"] = Capture("next-only:1:next=")
  });

  public static ScriptedAcquisition SecondPageRedirectsToFirst() => new(new()
  {
    ["/favorites?page=1"] = Capture("favorites:1:last=2:threads="),
    ["/favorites?page=2"] = _ => Task.FromResult<PageAcquisitionResult>(new PageCaptured(new PageSnapshot(
        new Uri("https://fixture.invalid/favorites?page=2"),
        new Uri("https://fixture.invalid/favorites?page=1"),
        "text/html",
        "favorites:1:last=2:threads=",
        DateTimeOffset.UtcNow)))
  });

  public static ScriptedAcquisition SecondPageClaimsFirst() => new(new()
  {
    ["/favorites?page=1"] = Capture("favorites:1:last=2:threads="),
    ["/favorites?page=2"] = Capture("favorites:1:last=2:threads=")
  });

  public static ScriptedAcquisition Blocking() => new(new()
  {
    ["/favorites?page=1"] = async cancellationToken =>
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      throw new InvalidOperationException("Unreachable fixture continuation.");
    }
  });

  public static ScriptedAcquisition DelayedSuccess() => new(new()
  {
    ["/favorites?page=1"] = async cancellationToken =>
    {
      await Task.Delay(100, cancellationToken);
      return Snapshot("favorites:1:last=1:threads=");
    }
  });

  private static Func<CancellationToken, Task<PageAcquisitionResult>> Capture(string text) =>
      _ => Task.FromResult(Snapshot(text));

  private static PageAcquisitionResult Snapshot(string text) => new PageCaptured(new PageSnapshot(
      new Uri("https://fixture.invalid/"),
      new Uri("https://fixture.invalid/"),
      "text/html",
      text,
      DateTimeOffset.UtcNow));
}

sealed class MirrorFallbackAcquisition : IPageAcquisitionAdapter
{
  public List<Uri> Requests { get; } = [];

  public ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    Requests.Add(request.Uri);
    if (request.Uri.Host.Equals("fixture.invalid", StringComparison.OrdinalIgnoreCase))
    {
      return ValueTask.FromResult<PageAcquisitionResult>(new PageUnavailable(
          CollectionIssue.Resumable(
              CollectionErrorCode.NavigationFailed,
              CollectionStage.Acquire,
              "fixture primary route failed",
              request.Uri)));
    }

    return ValueTask.FromResult<PageAcquisitionResult>(new PageCaptured(new PageSnapshot(
        request.Uri,
        request.Uri,
        "text/html",
        "favorites:1:last=1:threads=",
        DateTimeOffset.UtcNow,
        request.Kind)));
  }
}

sealed class FakeNetworkRecovery : INetworkRecoveryAdapter
{
  public int CallCount { get; private set; }
  public bool Recovered { get; private set; }

  public ValueTask<NetworkRecoveryResult> TryRecoverAsync(
      NetworkFailureContext failure,
      CancellationToken cancellationToken)
  {
    CallCount++;
    Recovered = true;
    return ValueTask.FromResult(new NetworkRecoveryResult(true, "fixture route recovered"));
  }
}

sealed class RecoveryAwareAcquisition(FakeNetworkRecovery recovery) : IPageAcquisitionAdapter
{
  public List<Uri> Requests { get; } = [];

  public ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    Requests.Add(request.Uri);
    if (!recovery.Recovered)
    {
      return ValueTask.FromResult<PageAcquisitionResult>(new PageUnavailable(
          CollectionIssue.Resumable(
              CollectionErrorCode.NavigationFailed,
              CollectionStage.Acquire,
              "fixture origins failed",
              request.Uri)));
    }

    return ValueTask.FromResult<PageAcquisitionResult>(new PageCaptured(new PageSnapshot(
        request.Uri,
        request.Uri,
        "text/html",
        "favorites:1:last=1:threads=",
        DateTimeOffset.UtcNow,
        request.Kind)));
  }
}

sealed class ThrowingNetworkRecovery : INetworkRecoveryAdapter
{
  public ValueTask<NetworkRecoveryResult> TryRecoverAsync(
      NetworkFailureContext failure,
      CancellationToken cancellationToken) =>
      ValueTask.FromException<NetworkRecoveryResult>(new HttpRequestException("fixture controller offline"));
}

sealed class AlwaysUnavailableAcquisition : IPageAcquisitionAdapter
{
  public ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken) =>
      ValueTask.FromResult<PageAcquisitionResult>(new PageUnavailable(
          CollectionIssue.Resumable(
              CollectionErrorCode.NavigationFailed,
              CollectionStage.Acquire,
              "fixture route unavailable",
              request.Uri,
              request.ExpectedThread)));
}

sealed class ScriptedForumAdapter : IForumSourceAdapter
{
  public FavoritePageProjection ParseFavoritePage(PageSnapshot snapshot, SiteDefinition site)
  {
    var values = snapshot.Document.Split(':');
    var page = int.Parse(values[1]);
    var last = int.Parse(values[2].Split('=')[1]);
    var ids = values[3].Split('=')[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
    return new FavoritePageProjection(
        CurrentPage: page,
        DeclaredLastPage: last,
        DiscoveredPageUris: Enumerable.Range(1, last)
            .Select(i => new Uri(site.PrimaryOrigin, $"/favorites?page={i}"))
            .ToImmutableArray(),
        Threads: ids.Select(id => new FavoriteReference(
            new ThreadKey(site.Family, id),
            new Uri(site.PrimaryOrigin, $"/thread/{id}")))
            .ToImmutableArray());
  }

  public ThreadProjection ParseThreadPage(PageSnapshot snapshot, ThreadKey expectedThread) =>
      new(expectedThread, $"Thread {expectedThread.ThreadId}");
}

sealed class IncompleteEvidenceForumAdapter : IForumSourceAdapter
{
  public FavoritePageProjection ParseFavoritePage(PageSnapshot snapshot, SiteDefinition site)
  {
    var values = snapshot.Document.Split(':');
    var page = int.Parse(values[1]);
    return new FavoritePageProjection(
        CurrentPage: page,
        DeclaredLastPage: 3,
        DiscoveredPageUris: [new Uri(site.PrimaryOrigin, $"/favorites?page={page}")],
        Threads: ImmutableArray<FavoriteReference>.Empty);
  }

  public ThreadProjection ParseThreadPage(PageSnapshot snapshot, ThreadKey expectedThread) =>
      throw new InvalidOperationException("No thread pages expected.");
}

sealed class NextOnlyForumAdapter : IForumSourceAdapter
{
  public FavoritePageProjection ParseFavoritePage(PageSnapshot snapshot, SiteDefinition site)
  {
    var values = snapshot.Document.Split(':');
    var page = int.Parse(values[1]);
    var nextValue = values[2].Split('=')[1];
    var next = string.IsNullOrEmpty(nextValue)
        ? null
        : new Uri(site.PrimaryOrigin, $"/favorites?page={nextValue}");
    return new FavoritePageProjection(
        CurrentPage: page,
        DeclaredLastPage: null,
        DiscoveredPageUris: ImmutableArray<Uri>.Empty,
        Threads: ImmutableArray<FavoriteReference>.Empty,
        NextUri: next,
        HasExplicitTerminalEvidence: next is null && page == 2);
  }

  public ThreadProjection ParseThreadPage(PageSnapshot snapshot, ThreadKey expectedThread) =>
      throw new InvalidOperationException("No thread pages expected.");
}

sealed class StaticImageHandler : HttpMessageHandler
{
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var isImage = request.RequestUri!.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
      Content = new ByteArrayContent(isImage ? [137, 80, 78, 71, 13, 10, 26, 10] : [80, 75, 3, 4])
    };
    response.Content.Headers.ContentType = new(isImage ? "image/png" : "application/zip");
    return Task.FromResult(response);
  }
}

sealed class BlockingImageHandler : HttpMessageHandler
{
  public TaskCompletionSource Started { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

  protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
  {
    Started.TrySetResult();
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    throw new InvalidOperationException("Unreachable fixture continuation.");
  }
}

sealed class TimeoutImageHandler : HttpMessageHandler
{
  protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken) =>
      Task.FromException<HttpResponseMessage>(new TaskCanceledException("Fixture image timeout."));
}

sealed class RecordingMihomoHandler : HttpMessageHandler
{
  public HttpRequestMessage? Request { get; private set; }
  public string? Body { get; private set; }

  protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
  {
    Request = request;
    Body = request.Content is null
        ? null
        : await request.Content.ReadAsStringAsync(cancellationToken);
    return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
  }
}

sealed class InteractiveAcquisition : IPageAcquisitionAdapter, IInteractivePageAcquisitionAdapter
{
  private bool _verified;
  public int UserActionCount { get; private set; }

  public ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken) =>
      ValueTask.FromResult<PageAcquisitionResult>(_verified
          ? new PageCaptured(new PageSnapshot(
              request.Uri,
              request.Uri,
              "text/html",
              "favorites:1:last=1:threads=",
              DateTimeOffset.UtcNow))
          : new PageNeedsUser(
              CollectionErrorCode.AuthenticationRequired,
              request.Uri,
              "fixture login required"));

  public ValueTask<bool> ResolveUserActionAsync(
      PageRequest request,
      PageNeedsUser prompt,
      CancellationToken cancellationToken)
  {
    UserActionCount++;
    _verified = true;
    return ValueTask.FromResult(true);
  }
}

sealed class ConcurrentThreadAcquisition : IPageAcquisitionAdapter
{
  private int _active;
  private int _maximum;
  public int MaximumConcurrentThreads => _maximum;

  public async ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    if (request.Kind == PageKind.FavoriteIndex)
    {
      return new PageCaptured(new PageSnapshot(
          request.Uri,
          request.Uri,
          "text/html",
          "favorites:1:last=1:threads=1,2,3",
          DateTimeOffset.UtcNow));
    }

    var active = Interlocked.Increment(ref _active);
    int observed;
    do
    {
      observed = _maximum;
    } while (active > observed && Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
    try
    {
      await Task.Delay(100, cancellationToken);
      return new PageCaptured(new PageSnapshot(
          request.Uri,
          request.Uri,
          "text/html",
          $"thread:{request.ExpectedThread!.Value.ThreadId}",
          DateTimeOffset.UtcNow));
    }
    finally
    {
      Interlocked.Decrement(ref _active);
    }
  }
}

sealed class ConcurrentFavoritePageAcquisition : IPageAcquisitionAdapter
{
  private int _active;
  private int _maximum;
  public int MaximumConcurrentFavoritePages => _maximum;

  public async ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    if (request.Kind != PageKind.FavoriteIndex)
    {
      throw new InvalidOperationException("No thread pages expected.");
    }

    var page = PaginationFrontier.LogicalPageNumber(request.Uri);
    if (page > 1)
    {
      var active = Interlocked.Increment(ref _active);
      int observed;
      do
      {
        observed = _maximum;
      } while (active > observed &&
               Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
      try
      {
        await Task.Delay(100, cancellationToken);
      }
      finally
      {
        Interlocked.Decrement(ref _active);
      }
    }

    return new PageCaptured(new PageSnapshot(
        request.Uri,
        request.Uri,
        "text/html",
        $"favorites:{page}:last=4:threads=",
        DateTimeOffset.UtcNow,
        request.Kind));
  }
}

sealed class ExplodingFavoritePrefetchAcquisition : IPageAcquisitionAdapter
{
  private readonly CancellationTokenSource _cleanup = new();
  private readonly TaskCompletionSource _bothPrefetchesStarted =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int _activeNavigations;
  private int _startedPrefetches;

  public int ActiveNavigations => Volatile.Read(ref _activeNavigations);
  public bool EngineCancellationObserved { get; private set; }
  public TaskCompletionSource AllNavigationsStopped { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

  public async ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    var page = PaginationFrontier.LogicalPageNumber(request.Uri);
    if (page == 1)
    {
      return Snapshot(request, "favorites:1:last=3:threads=");
    }

    Interlocked.Increment(ref _activeNavigations);
    if (Interlocked.Increment(ref _startedPrefetches) == 2)
    {
      _bothPrefetchesStarted.TrySetResult();
    }

    try
    {
      await _bothPrefetchesStarted.Task.WaitAsync(cancellationToken);
      if (page == 2)
      {
        throw new InvalidOperationException("fixture favorite navigation exploded");
      }

      using var linked = CancellationTokenSource.CreateLinkedTokenSource(
          cancellationToken,
          _cleanup.Token);
      try
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
      }
      catch (OperationCanceledException)
      {
        EngineCancellationObserved = cancellationToken.IsCancellationRequested;
        throw;
      }

      throw new InvalidOperationException("Unreachable fixture continuation.");
    }
    finally
    {
      if (Interlocked.Decrement(ref _activeNavigations) == 0)
      {
        AllNavigationsStopped.TrySetResult();
      }
    }
  }

  public void ReleaseForCleanup() => _cleanup.Cancel();

  private static PageAcquisitionResult Snapshot(PageRequest request, string document) =>
      new PageCaptured(new PageSnapshot(
          request.Uri,
          request.Uri,
          "text/html",
          document,
          DateTimeOffset.UtcNow,
          request.Kind));
}

sealed class OneExplodingThreadAcquisition : IPageAcquisitionAdapter
{
  private readonly TaskCompletionSource _bothThreadsStarted =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int _startedThreads;

  public async ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    if (request.Kind == PageKind.FavoriteIndex)
    {
      return Snapshot(request, "favorites:1:last=1:threads=1,2");
    }

    if (Interlocked.Increment(ref _startedThreads) == 2)
    {
      _bothThreadsStarted.TrySetResult();
    }
    await _bothThreadsStarted.Task.WaitAsync(cancellationToken);
    if (request.ExpectedThread?.ThreadId == "1")
    {
      throw new InvalidOperationException("fixture thread navigation exploded");
    }

    await Task.Delay(20, cancellationToken);
    return Snapshot(request, "thread:2");
  }

  private static PageAcquisitionResult Snapshot(PageRequest request, string document) =>
      new PageCaptured(new PageSnapshot(
          request.Uri,
          request.Uri,
          "text/html",
          document,
          DateTimeOffset.UtcNow,
          request.Kind));
}

sealed class DiscuzSemanticAcquisition(
    Uri? favoriteFinalUri = null,
    Uri? threadFinalUri = null,
    bool errorFavoritePage = false) : IPageAcquisitionAdapter
{
  public ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    var isFavorite = request.Kind == PageKind.FavoriteIndex;
    var finalUri = isFavorite
        ? favoriteFinalUri ?? request.Uri
        : threadFinalUri ?? request.Uri;
    var document = isFavorite
        ? errorFavoritePage ? "503 Service Unavailable" : "semantic-favorite"
        : $"thread:{request.ExpectedThread!.Value.ThreadId}";
    return ValueTask.FromResult<PageAcquisitionResult>(new PageCaptured(new PageSnapshot(
        request.Uri,
        finalUri,
        "text/html",
        document,
        DateTimeOffset.UtcNow,
        request.Kind)));
  }
}

sealed class DiscuzSemanticForumAdapter(bool includeThread, Uri? threadUri = null) : IForumSourceAdapter
{
  public FavoritePageProjection ParseFavoritePage(PageSnapshot snapshot, SiteDefinition site) =>
      new(
          CurrentPage: PaginationFrontier.LogicalPageNumber(snapshot.FinalUri),
          DeclaredLastPage: 1,
          DiscoveredPageUris: [snapshot.FinalUri],
          Threads: includeThread
              ? [new FavoriteReference(
                  new ThreadKey(site.Family, "42"),
                  threadUri ?? new Uri(site.PrimaryOrigin, "/forum.php?mod=viewthread&tid=42"))]
              : ImmutableArray<FavoriteReference>.Empty,
          NextUri: null,
          HasExplicitTerminalEvidence: true);

  public ThreadProjection ParseThreadPage(PageSnapshot snapshot, ThreadKey expectedThread) =>
      new(expectedThread, $"Thread {expectedThread.ThreadId}");
}

sealed class BlockingThreadHtmlAcquisition : IPageAcquisitionAdapter
{
  private readonly HtmlAcquisition _inner = new();
  public TaskCompletionSource ThreadStarted { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

  public async ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    if (request.Kind == PageKind.FavoriteIndex)
    {
      return await _inner.AcquireAsync(request, cancellationToken);
    }

    ThreadStarted.TrySetResult();
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    throw new InvalidOperationException("Unreachable fixture continuation.");
  }
}

sealed class HtmlAcquisition : IPageAcquisitionAdapter
{
  public ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    var html = request.Kind == PageKind.FavoriteIndex
        ? """
              <!doctype html><html><head><title>收藏</title></head><body>
              <main id="ct"><ul id="favorite_ul"><li id="favorite_li_77">
              <a href="/thread-77-1-1.html">纵向切片收藏</a><span>2026-08-24 12:00</span>
              </li></ul></main><div class="pg"><strong>1</strong></div>
              </body></html>
              """
        : """
              <!doctype html><html><head><title>纵向切片收藏 - 论坛</title>
              <link rel="canonical" href="https://fixture.invalid/thread-77-1-1.html"></head><body>
              <h1 id="thread_subject">纵向切片收藏</h1>
              <div id="postlist"><div id="post_1"><div class="pcb"><div id="postmessage_1" class="t_f">
              正文内容 解压密码: SAVE-77
              <img file="https://images.fixture.invalid/cover.png" width="800" height="600" alt="封面">
              <p>主资源 <a href="https://files.fixture.invalid/share">下载地址</a> 提取码: 2jsu</p>
              </div></div></div></div></body></html>
              """;
    return ValueTask.FromResult<PageAcquisitionResult>(new PageCaptured(new PageSnapshot(
        request.Uri,
        request.Uri,
        "text/html; charset=utf-8",
        html,
        DateTimeOffset.UtcNow)));
  }
}
