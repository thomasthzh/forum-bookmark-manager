using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Rfbm.Collection.Acquisition;
using Rfbm.Collection.Adapters;
using Rfbm.Collection.Contracts;
using Rfbm.Collection.Engine;
using Rfbm.Collection.Network;
using Rfbm.Collection.Playwright;
using Rfbm.BrowserBridge.Loopback;
using Rfbm.Core.Models;
using Rfbm.Data.Storage;
using Rfbm.Native.Services;
using Rfbm.Native.ViewModels;

namespace Rfbm.Native;

public partial class MainWindow : Window
{
  private readonly BookmarkDatabase _database;
  private readonly MainWindowViewModel _viewModel;
  private readonly CancellationTokenSource _windowLifetime = new();
  private ICollectionSession? _collectionSession;
  private PlaywrightEdgeAcquisitionAdapter? _browserAdapter;
  private HttpClient? _networkRecoveryHttpClient;
  private LoopbackBridgeReceiver? _bridgeReceiver;
  private Task? _collectionObserver;
  private bool _allowClose;

  public MainWindow()
  {
    _database = new BookmarkDatabase(AppDataPaths.DatabasePath());
    _viewModel = new MainWindowViewModel(new PostRepository(_database));

    InitializeComponent();
    DataContext = _viewModel;
  }

  private async void Window_Loaded(object sender, RoutedEventArgs e)
  {
    await RunUiActionAsync(async () =>
    {
      await _database.InitializeAsync(_windowLifetime.Token);
      await _viewModel.LoadAsync(_windowLifetime.Token);
    });
  }

  private async void Refresh_Click(object sender, RoutedEventArgs e) =>
      await RunUiActionAsync(() => _viewModel.LoadAsync(_windowLifetime.Token));

  private async void Random_Click(object sender, RoutedEventArgs e)
  {
    await _viewModel.RediscoverRandomAsync(_windowLifetime.Token);
    if (_viewModel.SelectedPost is not null)
    {
      PostGrid.SelectedItem = _viewModel.SelectedPost;
      PostGrid.ScrollIntoView(_viewModel.SelectedPost);
    }
  }

  private async void StartCollection_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.IsCollectionRunning)
    {
      _viewModel.ReportCollectionError("已有采集任务正在运行。");
      return;
    }

    var setupWindow = new CollectionSetupWindow { Owner = this };
    if (setupWindow.ShowDialog() != true || setupWindow.Result is not { } setup)
    {
      return;
    }

    await RunUiActionAsync(async () =>
    {
      _viewModel.BeginCollection();
      CancelCollectionButton.IsEnabled = true;

      _browserAdapter = new PlaywrightEdgeAcquisitionAdapter(new PlaywrightEdgeOptions(
              AppDataPaths.EdgeProfilePath(),
              Headless: true));

      if (setup.VerifyFirst)
      {
        _viewModel.ReportCollectionError("请在可见 Edge 中完成登录；完成后关闭验证页。");
        var preflightRequest = new PageRequest(
                new CollectionRunId(Guid.NewGuid()),
                PageKind.FavoriteIndex,
                setup.FavoritesUrl,
                null,
                8 * 1024 * 1024);
        var verified = await _browserAdapter.ResolveUserActionAsync(
                preflightRequest,
                new PageNeedsUser(
                    CollectionErrorCode.AuthenticationRequired,
                    setup.FavoritesUrl,
                    "等待人工登录验证"),
                _windowLifetime.Token);
        if (!verified)
        {
          throw new InvalidOperationException("可见 Edge 验证未完成，采集尚未启动。");
        }
        _viewModel.BeginCollection();
      }

      var primaryOrigin = new Uri(setup.FavoritesUrl.GetLeftPart(UriPartial.Authority));
      var plan = new CollectionPlan(
              AppDataPaths.DatabasePath(),
              new SiteDefinition(
                  new SiteFamily(setup.SiteFamily),
                  primaryOrigin,
                  setup.MirrorOrigins),
              setup.FavoritesUrl,
              FavoritePageConcurrency: 2,
              ThreadConcurrency: setup.ThreadConcurrency,
              MediaDirectory: AppDataPaths.ImagesPath());
      INetworkRecoveryAdapter? networkRecovery = null;
      if (setup.Mihomo is { } mihomo)
      {
        _networkRecoveryHttpClient = new HttpClient { BaseAddress = mihomo.Controller };
        networkRecovery = new MihomoNetworkRecoveryAdapter(
                _networkRecoveryHttpClient,
                mihomo.Secret,
                mihomo.Group,
                mihomo.Proxy);
      }
      var engine = new CollectionEngine(
              _browserAdapter,
              new DiscuzForumSourceAdapter(),
              networkRecovery: networkRecovery);

      CollectionIntent intent = new StartCollection(plan);
      var resume = await engine.FindResumeCandidateAsync(
              AppDataPaths.DatabasePath(),
              _windowLifetime.Token);
      if (resume is { CanResume: true } && MessageBox.Show(
                  this,
                  "发现上次未完成的采集。是否从本地检查点继续？",
                  "恢复采集",
                  MessageBoxButton.YesNo,
                  MessageBoxImage.Question) == MessageBoxResult.Yes)
      {
        intent = new ResumeCollection(AppDataPaths.DatabasePath(), resume.Id);
      }

      _collectionSession = await engine.OpenAsync(intent, _windowLifetime.Token);
      _collectionObserver = ObserveCollectionAsync(
              _collectionSession,
              _browserAdapter,
              _windowLifetime.Token);
    });
  }

  private async void Bridge_Click(object sender, RoutedEventArgs e)
  {
    await RunUiActionAsync(async () =>
    {
      if (_bridgeReceiver is not null)
      {
        await _bridgeReceiver.DisposeAsync();
        _bridgeReceiver = null;
        BridgeButton.Content = "⇄  扩展桥接";
        _viewModel.ReportStatus("Edge 扩展桥接已关闭。");
        return;
      }

      var setupWindow = new BridgeSetupWindow { Owner = this };
      if (setupWindow.ShowDialog() != true || setupWindow.Result is not { } setup)
      {
        return;
      }

      var processor = new BridgeInboxProcessor(
              new PostRepository(_database),
              AppDataPaths.SelectorProfilesPath(),
              setup.SiteFamily);
      _bridgeReceiver = await LoopbackBridgeReceiver.StartAsync(
              async (message, cancellationToken) =>
              {
                var result = await processor.ProcessAsync(message, cancellationToken);
                await Dispatcher.InvokeAsync(() => _viewModel.ReportStatus(result.SafeMessage));
                if (result.ImportedPosts > 0)
                {
                  var refreshTask = await Dispatcher.InvokeAsync(
                          () => _viewModel.LoadAsync(cancellationToken));
                  await refreshTask;
                }
              },
              new LoopbackBridgeReceiverOptions
              {
                Port = 53112,
                AllowedOrigins = [setup.AllowedOrigin]
              },
              _windowLifetime.Token);
      BridgeButton.Content = "●  扩展桥接已开启";
      _viewModel.ReportStatus("扩展桥接正在监听 127.0.0.1:53112；可在 Edge 工具栏手动发送当前页。");
    });
  }

  private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
  {
    var importWindow = new LegacyImportWindow { Owner = this };
    if (importWindow.ShowDialog() != true || importWindow.Result is not { } setup)
    {
      return;
    }

    await RunUiActionAsync(async () =>
    {
      var importer = new LegacyPythonDatabaseImporter(_database);
      var result = await importer.ImportAsync(
              setup.SourcePath,
              url =>
              {
                var key = Rfbm.Core.Models.ThreadKey.Parse(url, setup.SiteFamily);
                return new PostKey(key.SiteFamily, key.ThreadId);
              },
              _windowLifetime.Token);
      await _viewModel.LoadAsync(_windowLifetime.Token);
      _viewModel.ReportStatus($"已从旧版数据库只读导入 {result.ImportedCount} 条收藏。");
    });
  }

  private async Task ObserveCollectionAsync(
      ICollectionSession session,
      PlaywrightEdgeAcquisitionAdapter browser,
      CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var item in session.Events(cancellationToken))
      {
        await Dispatcher.InvokeAsync(() => _viewModel.ApplyCollectionEvent(item));
      }

      var result = await session.Completion;
      await Dispatcher.InvokeAsync(() =>
      {
        _viewModel.EndCollection(result);
        CancelCollectionButton.IsEnabled = false;
      });
      var refreshTask = await Dispatcher.InvokeAsync(
          () => _viewModel.LoadAsync(cancellationToken));
      await refreshTask;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception)
    {
      await Dispatcher.InvokeAsync(() =>
      {
        _viewModel.ReportCollectionError("采集会话异常结束；本地检查点已保留。");
        CancelCollectionButton.IsEnabled = false;
      });
    }
    finally
    {
      await browser.DisposeAsync();
      await session.DisposeAsync();
      _networkRecoveryHttpClient?.Dispose();
      _networkRecoveryHttpClient = null;
      if (ReferenceEquals(_collectionSession, session))
      {
        _collectionSession = null;
        _browserAdapter = null;
      }
    }
  }

  private async void CancelCollection_Click(object sender, RoutedEventArgs e)
  {
    if (_collectionSession is not null)
    {
      await RunUiActionAsync(() => _collectionSession.RequestCancelAsync().AsTask());
    }
  }

  private async void FilterAll_Click(object sender, RoutedEventArgs e)
  {
    await _viewModel.ShowLibraryAsync(_windowLifetime.Token);
    UpdateTrashActions();
  }

  private async void FilterStarred_Click(object sender, RoutedEventArgs e)
  {
    await _viewModel.ShowStarredAsync(_windowLifetime.Token);
    UpdateTrashActions();
  }

  private async void FilterStatus_Click(object sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: string value } && Enum.TryParse<PostStatus>(value, out var status))
    {
      await _viewModel.FilterStatusAsync(status, _windowLifetime.Token);
    }
    UpdateTrashActions();
  }

  private async void FilterType_Click(object sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: string value })
    {
      await _viewModel.FilterProjectTypeAsync(value, _windowLifetime.Token);
    }
    UpdateTrashActions();
  }

  private async void FilterTrash_Click(object sender, RoutedEventArgs e)
  {
    await _viewModel.ShowTrashAsync(_windowLifetime.Token);
    UpdateTrashActions();
  }

  private async void RowStar_Click(object sender, RoutedEventArgs e)
  {
    if (sender is Button { DataContext: PostListItem item })
    {
      await _viewModel.ToggleStarAsync(item, _windowLifetime.Token);
    }
  }

  private async void RowStatus_Click(object sender, RoutedEventArgs e)
  {
    if (sender is Button { DataContext: PostListItem item })
    {
      await _viewModel.CycleStatusAsync(item, _windowLifetime.Token);
    }
  }

  private async void BulkStatus_Click(object sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: string value } && Enum.TryParse<PostStatus>(value, out var status))
    {
      await _viewModel.SetSelectedStatusAsync(status, _windowLifetime.Token);
    }
  }

  private async void TrashSelected_Click(object sender, RoutedEventArgs e) =>
      await _viewModel.MoveSelectedToTrashAsync(_windowLifetime.Token);

  private async void RestoreSelected_Click(object sender, RoutedEventArgs e) =>
      await _viewModel.RestoreSelectedAsync(_windowLifetime.Token);

  private void PostGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (_viewModel.SelectedPost is { } item)
    {
      NoteEditor.Text = item.Note;
      TagsEditor.Text = string.Join(", ", item.Tags);
    }
    else
    {
      NoteEditor.Clear();
      TagsEditor.Clear();
    }
  }

  private async void SaveNote_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.SelectedPost is { } item)
    {
      await _viewModel.SaveNoteAsync(item, NoteEditor.Text, _windowLifetime.Token);
    }
  }

  private async void SaveTags_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.SelectedPost is { } item)
    {
      await _viewModel.SetTagsAsync(item, TagsEditor.Text, _windowLifetime.Token);
    }
  }

  private void OpenPost_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.SelectedPost is not { Url: { Length: > 0 } url })
    {
      return;
    }
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
  }

  private void CopyPassword_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.SelectedPost is { ExtractPassword.Length: > 0 } item)
    {
      Clipboard.SetText(item.ExtractPassword);
    }
  }

  private async void Window_Closing(object? sender, CancelEventArgs e)
  {
    if (!_allowClose && _viewModel.IsCollectionRunning && _collectionSession is not null)
    {
      e.Cancel = true;
      if (MessageBox.Show(
              this,
              "采集仍在运行。退出前将保存取消请求，稍后可以恢复。",
              "退出 R-fbm",
              MessageBoxButton.OKCancel,
              MessageBoxImage.Warning) != MessageBoxResult.OK)
      {
        return;
      }
      await _collectionSession.RequestCancelAsync();
      _allowClose = true;
      Close();
      return;
    }

    _windowLifetime.Cancel();
    if (_bridgeReceiver is not null)
    {
      await _bridgeReceiver.DisposeAsync();
      _bridgeReceiver = null;
    }
    _viewModel.Dispose();
  }

  private void UpdateTrashActions()
  {
    TrashActionButton.Visibility = _viewModel.ShowingTrash ? Visibility.Collapsed : Visibility.Visible;
    RestoreActionButton.Visibility = _viewModel.ShowingTrash ? Visibility.Visible : Visibility.Collapsed;
  }

  private async Task RunUiActionAsync(Func<Task> action)
  {
    try
    {
      await action();
    }
    catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
    {
    }
    catch (CollectionOpenException ex)
    {
      _viewModel.ReportCollectionError(ex.Message);
      CancelCollectionButton.IsEnabled = false;
    }
    catch (Exception)
    {
      _viewModel.ReportCollectionError("操作失败；未修改已有收藏数据。");
      CancelCollectionButton.IsEnabled = false;
    }
  }
}
