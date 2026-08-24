using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Rfbm.Core.Models;
using Rfbm.Collection.Contracts;
using Rfbm.Data.Storage;

namespace Rfbm.Native.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
  private readonly PostRepository _repository;
  private readonly SemaphoreSlim _reloadGate = new(1, 1);
  private CancellationTokenSource? _searchDebounce;
  private string _searchText = string.Empty;
  private string _statusText = "准备就绪";
  private string _workingSetText = "内存：-";
  private string _rediscoveryText = "让旧收藏重新遇见你";
  private PostListItem? _selectedPost;
  private PostStatus? _statusFilter;
  private string? _projectTypeFilter;
  private bool _onlyStarred;
  private bool _showTrash;
  private bool _isBusy;
  private bool _isCollectionRunning;
  private string _collectionProgressText = "采集器待命";
  private int _totalCount;

  public MainWindowViewModel(PostRepository repository)
  {
    _repository = repository;
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  public ObservableCollection<PostListItem> Posts { get; } = [];

  public string SearchText
  {
    get => _searchText;
    set
    {
      if (SetField(ref _searchText, value ?? string.Empty))
      {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new CancellationTokenSource();
        _ = DebouncedSearchAsync(_searchDebounce.Token);
      }
    }
  }

  public string StatusText
  {
    get => _statusText;
    private set => SetField(ref _statusText, value);
  }

  public string WorkingSetText
  {
    get => _workingSetText;
    private set => SetField(ref _workingSetText, value);
  }

  public string RediscoveryText
  {
    get => _rediscoveryText;
    private set => SetField(ref _rediscoveryText, value);
  }

  public PostListItem? SelectedPost
  {
    get => _selectedPost;
    set => SetField(ref _selectedPost, value);
  }

  public bool IsBusy
  {
    get => _isBusy;
    private set => SetField(ref _isBusy, value);
  }

  public bool IsEmpty => Posts.Count == 0;
  public bool ShowingTrash => _showTrash;
  public int TotalCount => _totalCount;
  public int SelectedCount => Posts.Count(post => post.IsSelected);

  public bool IsCollectionRunning
  {
    get => _isCollectionRunning;
    private set => SetField(ref _isCollectionRunning, value);
  }

  public string CollectionProgressText
  {
    get => _collectionProgressText;
    private set => SetField(ref _collectionProgressText, value);
  }

  public void BeginCollection()
  {
    IsCollectionRunning = true;
    StatusText = "正在建立采集会话";
    CollectionProgressText = "准备浏览器和本地检查点…";
  }

  public void ApplyCollectionEvent(CollectionEvent item)
  {
    switch (item)
    {
      case StateChanged state:
        StatusText = state.Message;
        CollectionProgressText = state.State switch
        {
          RunState.WaitingForUser => "等待你在可见 Edge 中完成操作",
          RunState.Discovering => "正在证明收藏分页完整性",
          RunState.Collecting => "正在采集详情与本地图片",
          _ => state.State.ToString()
        };
        break;
      case ProgressChanged progress:
        CollectionProgressText =
            $"分页 {progress.PagesCommitted}/{progress.PagesDiscovered} · " +
            $"帖子 {progress.ThreadsCommitted}/{progress.ThreadsDiscovered} · " +
            $"失败 {progress.PagesFailed + progress.ThreadsFailed}";
        break;
      case ItemFailed failed:
        StatusText = failed.Issue.SafeMessage;
        break;
    }
  }

  public void EndCollection(CollectionResult result)
  {
    IsCollectionRunning = false;
    StatusText = result.State switch
    {
      RunState.Succeeded => "采集完成",
      RunState.PartiallySucceeded => "采集部分完成，可从检查点恢复",
      RunState.Cancelled => "采集已取消，可稍后恢复",
      _ => "采集结束，请查看失败信息"
    };
    CollectionProgressText =
        $"分页 {result.PagesCommitted}/{result.PagesDiscovered} · " +
        $"帖子 {result.ThreadsCommitted}/{result.ThreadsDiscovered} · " +
        $"失败 {result.PagesFailed + result.ThreadsFailed}";
  }

  public void ReportCollectionError(string safeMessage)
  {
    IsCollectionRunning = false;
    StatusText = safeMessage;
    CollectionProgressText = "采集器已停止";
  }

  public void ReportStatus(string safeMessage)
  {
    StatusText = safeMessage;
  }

  public async Task LoadAsync(CancellationToken cancellationToken = default) =>
      await ReloadAsync(cancellationToken);

  public async Task ApplySearchAsync(string value, CancellationToken cancellationToken = default)
  {
    _searchDebounce?.Cancel();
    _searchText = value ?? string.Empty;
    OnPropertyChanged(nameof(SearchText));
    await ReloadAsync(cancellationToken);
  }

  public async Task FilterStatusAsync(PostStatus? status, CancellationToken cancellationToken = default)
  {
    _statusFilter = status;
    await ReloadAsync(cancellationToken);
  }

  public async Task FilterProjectTypeAsync(string? projectType, CancellationToken cancellationToken = default)
  {
    _projectTypeFilter = string.IsNullOrWhiteSpace(projectType) ? null : projectType.Trim();
    await ReloadAsync(cancellationToken);
  }

  public async Task ShowStarredAsync(CancellationToken cancellationToken = default)
  {
    _onlyStarred = true;
    _showTrash = false;
    await ReloadAsync(cancellationToken);
  }

  public async Task ShowLibraryAsync(CancellationToken cancellationToken = default)
  {
    _showTrash = false;
    _onlyStarred = false;
    _statusFilter = null;
    _projectTypeFilter = null;
    await ReloadAsync(cancellationToken);
  }

  public async Task ShowTrashAsync(CancellationToken cancellationToken = default)
  {
    _showTrash = true;
    _onlyStarred = false;
    await ReloadAsync(cancellationToken);
  }

  public async Task CycleStatusAsync(PostListItem item, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(item);
    var next = item.Status.Next();
    if (await _repository.SetStatusAsync(item.Key, next, cancellationToken))
    {
      item.Status = next;
      StatusText = $"“{item.Title}”已标记为{next.DisplayName()}";
    }
  }

  public async Task SetSelectedStatusAsync(PostStatus status, CancellationToken cancellationToken = default)
  {
    var selected = SelectedKeys();
    var count = await _repository.SetStatusManyAsync(selected, status, cancellationToken);
    if (count > 0)
    {
      foreach (var item in Posts.Where(post => post.IsSelected))
      {
        item.Status = status;
      }
    }
    StatusText = count == 0 ? "请先选择收藏" : $"已更新 {count} 条收藏";
  }

  public async Task ToggleStarAsync(PostListItem item, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(item);
    var next = !item.IsStarred;
    if (await _repository.SetStarredAsync(item.Key, next, cancellationToken))
    {
      item.IsStarred = next;
      StatusText = next ? "已加入星标" : "已取消星标";
    }
  }

  public async Task SaveNoteAsync(
      PostListItem item,
      string? note,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(item);
    var normalized = note?.Trim() ?? string.Empty;
    if (await _repository.SetNoteAsync(item.Key, normalized, cancellationToken))
    {
      item.Note = normalized;
      StatusText = "笔记已保存";
    }
  }

  public async Task SetTagsAsync(
      PostListItem item,
      string? tags,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(item);
    var values = (tags ?? string.Empty)
        .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (await _repository.ReplaceTagsAsync(item.Key, values, cancellationToken))
    {
      item.Tags = values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
      StatusText = "标签已保存";
    }
  }

  public async Task MoveSelectedToTrashAsync(CancellationToken cancellationToken = default)
  {
    var selected = SelectedKeys();
    var count = await _repository.MoveToTrashAsync(selected, cancellationToken);
    StatusText = count == 0 ? "请先选择收藏" : $"已将 {count} 条收藏移入回收站";
    if (count > 0)
    {
      await ReloadAsync(cancellationToken);
    }
  }

  public async Task RestoreSelectedAsync(CancellationToken cancellationToken = default)
  {
    var selected = SelectedKeys();
    var count = await _repository.RestoreAsync(selected, cancellationToken);
    StatusText = count == 0 ? "请先选择收藏" : $"已恢复 {count} 条收藏";
    if (count > 0)
    {
      await ReloadAsync(cancellationToken);
    }
  }

  public Task RediscoverRandomAsync(CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (Posts.Count == 0)
    {
      SelectedPost = null;
      RediscoveryText = "收藏库还是空的，先去发现宝藏吧";
      return Task.CompletedTask;
    }

    SelectedPost = Posts[Random.Shared.Next(Posts.Count)];
    RediscoveryText = $"今日重逢：{SelectedPost.Title}";
    return Task.CompletedTask;
  }

  public void RefreshMemoryText()
  {
    var workingSet = Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d;
    WorkingSetText = $"内存 {workingSet:F1} MB";
  }

  public void Dispose()
  {
    _searchDebounce?.Cancel();
    _searchDebounce?.Dispose();
    _reloadGate.Dispose();
  }

  private async Task DebouncedSearchAsync(CancellationToken cancellationToken)
  {
    try
    {
      await Task.Delay(250, cancellationToken);
      await ReloadAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
  }

  private async Task ReloadAsync(CancellationToken cancellationToken)
  {
    await _reloadGate.WaitAsync(cancellationToken);
    try
    {
      IsBusy = true;
      var page = await _repository.QueryAsync(new PostQuery
      {
        Search = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim(),
        ProjectType = _projectTypeFilter,
        Status = _statusFilter,
        OnlyStarred = _onlyStarred,
        OnlyTrashed = _showTrash,
        IncludeTrashed = _showTrash,
        Limit = 500
      }, cancellationToken);

      Posts.Clear();
      foreach (var post in page.Items)
      {
        Posts.Add(new PostListItem(post));
      }
      _totalCount = page.Total;
      SelectedPost = Posts.FirstOrDefault();
      StatusText = page.Total == 0
          ? (_showTrash ? "回收站是空的。" : "还没有收藏，先连接论坛开始采集。")
          : (_showTrash ? $"回收站 {page.Total} 条" : $"共 {page.Total} 条收藏");
      RefreshMemoryText();
      OnPropertyChanged(nameof(TotalCount));
      OnPropertyChanged(nameof(IsEmpty));
      OnPropertyChanged(nameof(ShowingTrash));
      OnPropertyChanged(nameof(SelectedCount));
    }
    finally
    {
      IsBusy = false;
      _reloadGate.Release();
    }
  }

  private IReadOnlyList<PostKey> SelectedKeys() =>
      Posts.Where(post => post.IsSelected).Select(post => post.Key).ToArray();

  private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
  {
    if (EqualityComparer<T>.Default.Equals(field, value))
    {
      return false;
    }
    field = value;
    OnPropertyChanged(propertyName);
    return true;
  }

  private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
