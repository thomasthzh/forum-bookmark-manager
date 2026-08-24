using Rfbm.Core.Models;
using Rfbm.Data.Storage;
using Rfbm.Native;
using Rfbm.Native.Services;
using Rfbm.Native.ViewModels;
using Rfbm.Tests;
using BridgePageKind = Rfbm.BrowserBridge.Contracts.PageKind;
using BridgePageSnapshot = Rfbm.BrowserBridge.Contracts.PageSnapshot;

var root = Path.Combine(Path.GetTempPath(), "rfbm-native-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "native.sqlite3");

try
{
  var previousDataDirectory = Environment.GetEnvironmentVariable("RFBM_DATA_DIR");
  try
  {
    var overriddenDataDirectory = Path.Combine(root, "isolated-app-data");
    Environment.SetEnvironmentVariable("RFBM_DATA_DIR", overriddenDataDirectory);
    TestAssert.Equal(Path.GetFullPath(overriddenDataDirectory), AppDataPaths.RootDirectory());
    WpfShellCanConstruct();
  }
  finally
  {
    Environment.SetEnvironmentVariable("RFBM_DATA_DIR", previousDataDirectory);
  }

  var database = new BookmarkDatabase(path);
  await database.InitializeAsync();
  var repository = new PostRepository(database);
  var viewModel = new MainWindowViewModel(repository);

  var bridgeDatabase = new BookmarkDatabase(Path.Combine(root, "bridge.sqlite3"));
  await bridgeDatabase.InitializeAsync();
  var bridgeProcessor = new BridgeInboxProcessor(
      new PostRepository(bridgeDatabase),
      Path.Combine(root, "selector-profiles"),
      "fixture-site");
  var bridgeResult = await bridgeProcessor.ProcessAsync(BridgePageSnapshot.Create(
      "fixture-nonce",
      BridgePageKind.Thread,
      "https://fixture.invalid/thread-303-1-1.html",
      "https://fixture.invalid/thread-303-1-1.html",
      "桥接帖子",
      """
        <!doctype html><title>桥接帖子 - 论坛</title>
        <link rel="canonical" href="https://fixture.invalid/thread-303-1-1.html">
        <h1 id="thread_subject">桥接帖子</h1>
        <div id="postlist"><div id="post_1"><div class="pcb"><div id="postmessage_1" class="t_f">
        桥接正文 解压密码: BRIDGE-303
        </div></div></div></div>
        """));
  TestAssert.Equal(1, bridgeResult.ImportedPosts);
  var bridgedPost = TestAssert.NotNull(await new PostRepository(bridgeDatabase).GetAsync(
      new PostKey("fixture-site", "303")));
  TestAssert.Equal("BRIDGE-303", bridgedPost.ExtractPassword);
  var selectorResult = await bridgeProcessor.ProcessAsync(
      Rfbm.BrowserBridge.Contracts.SelectorProfileMessage.Create(
          "fixture-nonce",
          "fixture.invalid",
          "https://fixture.invalid/thread-303-1-1.html",
          new Dictionary<string, string> { ["postBody"] = "#postmessage_1" }));
  TestAssert.Equal(0, selectorResult.ImportedPosts);
  var selectorPath = Path.Combine(root, "selector-profiles", "fixture.invalid.json");
  TestAssert.Equal(true, File.Exists(selectorPath));
  TestAssert.Equal(false, (await File.ReadAllTextAsync(selectorPath)).Contains(
      "fixture-nonce", StringComparison.Ordinal));

  TestAssert.Equal(true, MihomoSetup.TryCreate(
      "http://127.0.0.1:9090", "AUTO", "fixture-node", "secret", out var mihomo));
  TestAssert.Equal("AUTO", mihomo!.Group);
  TestAssert.Equal(false, MihomoSetup.TryCreate(
      "http://192.168.1.2:9090", "AUTO", "fixture-node", null, out _));
  TestAssert.Equal(true, BridgeSetupResult.TryCreate(
      "abcdefghijklmnopabcdefghijklmnop", "fixture-site", out var bridgeSetup));
  TestAssert.Equal(
      "chrome-extension://abcdefghijklmnopabcdefghijklmnop",
      bridgeSetup!.AllowedOrigin);
  TestAssert.Equal(false, BridgeSetupResult.TryCreate(
      "not-an-extension-id", "fixture-site", out _));

  await viewModel.LoadAsync();
  TestAssert.Equal(0, viewModel.Posts.Count);
  TestAssert.Equal(true, viewModel.IsEmpty);
  TestAssert.Equal("还没有收藏，先连接论坛开始采集。", viewModel.StatusText);

  var firstKey = new PostKey("fixture-site", "101");
  var secondKey = new PostKey("fixture-site", "102");
  await repository.UpsertCaptureAsync(Capture(firstKey, "本地工具箱", "工具", "alpha note"));
  await repository.UpsertCaptureAsync(Capture(secondKey, "怀旧游戏合集", "游戏", "retro note"));

  await viewModel.LoadAsync();
  TestAssert.Equal(2, viewModel.Posts.Count);
  TestAssert.Equal(false, viewModel.IsEmpty);
  TestAssert.Equal("共 2 条收藏", viewModel.StatusText);

  await viewModel.ApplySearchAsync("retro");
  TestAssert.Equal(1, viewModel.Posts.Count);
  TestAssert.Equal("怀旧游戏合集", viewModel.Posts[0].Title);

  await viewModel.ApplySearchAsync(string.Empty);
  var first = viewModel.Posts.Single(post => post.Key == firstKey);
  first.IsSelected = true;
  await viewModel.CycleStatusAsync(first);
  TestAssert.Equal(PostStatus.Downloaded, TestAssert.NotNull(await repository.GetAsync(firstKey)).Status);

  await viewModel.ToggleStarAsync(first);
  await viewModel.SaveNoteAsync(first, "人工整理笔记");
  await viewModel.SetTagsAsync(first, "精选, 工具, 精选");
  var edited = TestAssert.NotNull(await repository.GetAsync(firstKey));
  TestAssert.Equal(true, edited.IsStarred);
  TestAssert.Equal("人工整理笔记", edited.Note);
  TestAssert.Equal(2, edited.Tags.Count);

  await viewModel.MoveSelectedToTrashAsync();
  TestAssert.Equal(1, viewModel.Posts.Count);
  TestAssert.Equal<StoredPost?>(null, await repository.GetAsync(firstKey));

  await viewModel.ShowTrashAsync();
  TestAssert.Equal(1, viewModel.Posts.Count);
  viewModel.Posts[0].IsSelected = true;
  await viewModel.RestoreSelectedAsync();
  TestAssert.Equal(0, viewModel.Posts.Count);
  TestAssert.NotNull(await repository.GetAsync(firstKey));

  await viewModel.ShowLibraryAsync();
  await viewModel.RediscoverRandomAsync();
  TestAssert.NotNull(viewModel.SelectedPost);
  TestAssert.Equal(true, viewModel.RediscoveryText.StartsWith("今日重逢：", StringComparison.Ordinal));

  Console.WriteLine("Rfbm.Native.Tests passed.");
}
finally
{
  Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
  Directory.Delete(root, recursive: true);
}

static PostCapture Capture(PostKey key, string title, string type, string body) => new()
{
  Key = key,
  CanonicalUrl = $"https://fixture.invalid/thread-{key.ThreadId}-1-1.html",
  Title = Observation<string>.Observed(title),
  ProjectType = Observation<string>.Observed(type),
  BodyText = Observation<string?>.Observed(body),
  ExtractPassword = Observation<string?>.Observed("2jsu"),
  Images = Observation<IReadOnlyList<CapturedPostImage>>.Observed(
        [new CapturedPostImage(1, "https://images.fixture.invalid/cover.jpg")]),
  ResourceLinks = Observation<IReadOnlyList<CapturedResourceLink>>.Observed(
        [new CapturedResourceLink(1, "https://files.fixture.invalid/share", "资源", "提取码 2jsu")])
};

static void WpfShellCanConstruct()
{
  Exception? failure = null;
  var thread = new Thread(() =>
  {
    try
    {
      var application = new App
      {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
      };
      application.InitializeComponent();
      var windows = new System.Windows.Window[]
        {
                new MainWindow(),
                new CollectionSetupWindow(),
                new BridgeSetupWindow(),
                new LegacyImportWindow()
          };
      foreach (var window in windows)
      {
        if (window.Background is not System.Windows.Media.SolidColorBrush background ||
              background.Color != System.Windows.Media.Color.FromRgb(0x09, 0x11, 0x1F))
        {
          throw new InvalidOperationException(
                $"{window.GetType().Name} did not apply the dark application window style.");
        }
        window.Close();
      }
      application.Shutdown();
    }
    catch (Exception ex)
    {
      failure = ex;
    }
  });
  thread.SetApartmentState(ApartmentState.STA);
  thread.Start();
  if (!thread.Join(TimeSpan.FromSeconds(5)))
  {
    throw new TimeoutException("WPF shell construction did not finish.");
  }
  if (failure is not null)
  {
    throw new InvalidOperationException("WPF shell failed to construct.", failure);
  }
}
