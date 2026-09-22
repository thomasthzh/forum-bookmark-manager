using Microsoft.Playwright;
using Rfbm.Collection.Acquisition;
using Rfbm.Collection.Contracts;

namespace Rfbm.Collection.Playwright;

public sealed record PlaywrightEdgeOptions(
    string ProfileDirectory,
    bool Headless = true,
    int NavigationTimeoutMilliseconds = 60_000,
    string BrowserChannel = "msedge");

public sealed class PlaywrightEdgeAcquisitionAdapter :
    IPageAcquisitionAdapter,
    IInteractivePageAcquisitionAdapter,
    IAsyncDisposable
{
  private readonly PlaywrightEdgeOptions _options;
  private readonly SemaphoreSlim _contextGate = new(1, 1);
  private IPlaywright? _playwright;
  private IBrowserContext? _context;

  public PlaywrightEdgeAcquisitionAdapter(PlaywrightEdgeOptions options)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileDirectory);
    if (options.NavigationTimeoutMilliseconds is < 1_000 or > 300_000)
    {
      throw new ArgumentOutOfRangeException(nameof(options));
    }

    var profileDirectory = Path.GetFullPath(options.ProfileDirectory);
    BrowserProfilePathGuard.Validate(profileDirectory);
    _options = options with { ProfileDirectory = profileDirectory };
  }

  public async ValueTask<PageAcquisitionResult> AcquireAsync(
      PageRequest request,
      CancellationToken cancellationToken)
  {
    try
    {
      var context = await GetContextAsync(cancellationToken);
      var page = await context.NewPageAsync();
      try
      {
        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
              var cancellablePage = (IPage)state!;
              _ = cancellablePage.CloseAsync();
            },
            page);
        var response = await page.GotoAsync(request.Uri.ToString(), new PageGotoOptions
        {
          WaitUntil = WaitUntilState.DOMContentLoaded,
          Timeout = _options.NavigationTimeoutMilliseconds
        });
        if (response is null)
        {
          return new PageUnavailable(CollectionIssue.Resumable(
              CollectionErrorCode.NavigationFailed,
              CollectionStage.Acquire,
              "浏览器没有返回页面响应。",
              request.Uri,
              request.ExpectedThread));
        }

        if (response.Status is 401 or 403)
        {
          return new PageNeedsUser(
              CollectionErrorCode.AuthenticationRequired,
              request.Uri,
              "页面要求人工登录或权限确认。");
        }
        if (response.Status == 429 || response.Status >= 500)
        {
          return new PageUnavailable(CollectionIssue.Resumable(
              CollectionErrorCode.NavigationFailed,
              CollectionStage.Acquire,
              "站点暂时不可用，可稍后恢复。",
              request.Uri,
              request.ExpectedThread));
        }

        var mediaType = await page.EvaluateAsync<string>("() => document.contentType || ''");
        if (!mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
          return new PageUnavailable(CollectionIssue.Permanent(
              CollectionErrorCode.SnapshotRejected,
              CollectionStage.VerifySnapshot,
              "浏览器响应不是 HTML 页面。",
              request.Uri,
              request.ExpectedThread));
        }

        var document = await page.ContentAsync();
        var classification = PlaywrightSnapshotClassifier.Classify(document);
        if (classification == SnapshotClassification.LoginRequired)
        {
          return new PageNeedsUser(
              CollectionErrorCode.AuthenticationRequired,
              request.Uri,
              "需要在可见 Edge 页面中完成人工登录。");
        }
        if (classification == SnapshotClassification.HumanVerificationRequired)
        {
          return new PageNeedsUser(
              CollectionErrorCode.HumanVerificationRequired,
              request.Uri,
              "需要在可见 Edge 页面中完成人工验证。");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(document) > request.MaxSnapshotBytes)
        {
          return new PageUnavailable(CollectionIssue.Permanent(
              CollectionErrorCode.SnapshotRejected,
              CollectionStage.VerifySnapshot,
              "页面快照超过安全上限。",
              request.Uri,
              request.ExpectedThread));
        }

        var finalUri = new Uri(page.Url);
        var snapshot = new PageSnapshot(
            request.Uri,
            finalUri,
            mediaType,
            document,
            DateTimeOffset.UtcNow,
            request.Kind);
        return new PageCaptured(snapshot with { Sha256 = snapshot.ComputedSha256 });
      }
      finally
      {
        if (!page.IsClosed)
        {
          await page.CloseAsync();
        }
      }
    }
    catch (PlaywrightException) when (cancellationToken.IsCancellationRequested)
    {
      throw new OperationCanceledException(cancellationToken);
    }
    catch (PlaywrightException)
    {
      return new PageUnavailable(CollectionIssue.Resumable(
          CollectionErrorCode.NavigationFailed,
          CollectionStage.Acquire,
          "Edge 页面采集失败，可稍后恢复。",
          request.Uri,
          request.ExpectedThread));
    }
  }

  public async ValueTask DisposeAsync()
  {
    await _contextGate.WaitAsync();
    try
    {
      if (_context is not null)
      {
        await _context.CloseAsync();
        _context = null;
      }
      _playwright?.Dispose();
      _playwright = null;
    }
    finally
    {
      _contextGate.Release();
      _contextGate.Dispose();
    }
  }

  public async ValueTask<bool> ResolveUserActionAsync(
      PageRequest request,
      PageNeedsUser prompt,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    await CloseContextAsync();
    IPlaywright? visiblePlaywright = null;
    IBrowserContext? visibleContext = null;
    try
    {
      visiblePlaywright = await Microsoft.Playwright.Playwright.CreateAsync()
          .WaitAsync(cancellationToken);
      visibleContext = await visiblePlaywright.Chromium.LaunchPersistentContextAsync(
          _options.ProfileDirectory,
          new BrowserTypeLaunchPersistentContextOptions
          {
            AcceptDownloads = false,
            Channel = _options.BrowserChannel,
            Headless = false
          }).WaitAsync(cancellationToken);
      var page = visibleContext.Pages.FirstOrDefault() ?? await visibleContext.NewPageAsync();
      var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      page.Close += (_, _) => closed.TrySetResult();
      using var cancellationRegistration = cancellationToken.Register(
          static state => _ = ((IPage)state!).CloseAsync(),
          page);
      await page.GotoAsync(request.Uri.ToString(), new PageGotoOptions
      {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = _options.NavigationTimeoutMilliseconds
      }).WaitAsync(cancellationToken);
      await closed.Task.WaitAsync(cancellationToken);
      return true;
    }
    catch (PlaywrightException) when (cancellationToken.IsCancellationRequested)
    {
      throw new OperationCanceledException(cancellationToken);
    }
    catch (PlaywrightException)
    {
      return false;
    }
    finally
    {
      if (visibleContext is not null)
      {
        await visibleContext.CloseAsync();
      }
      visiblePlaywright?.Dispose();
    }
  }

  private async Task<IBrowserContext> GetContextAsync(CancellationToken cancellationToken)
  {
    if (_context is not null)
    {
      return _context;
    }

    await _contextGate.WaitAsync(cancellationToken);
    try
    {
      if (_context is not null)
      {
        return _context;
      }

      Directory.CreateDirectory(_options.ProfileDirectory);
      _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
      _context = await _playwright.Chromium.LaunchPersistentContextAsync(
          _options.ProfileDirectory,
          new BrowserTypeLaunchPersistentContextOptions
          {
            AcceptDownloads = false,
            Channel = _options.BrowserChannel,
            Headless = _options.Headless
          });
      _context.SetDefaultNavigationTimeout(_options.NavigationTimeoutMilliseconds);
      return _context;
    }
    finally
    {
      _contextGate.Release();
    }
  }

  private async Task CloseContextAsync()
  {
    await _contextGate.WaitAsync();
    try
    {
      if (_context is not null)
      {
        await _context.CloseAsync();
        _context = null;
      }
      _playwright?.Dispose();
      _playwright = null;
    }
    finally
    {
      _contextGate.Release();
    }
  }
}

internal static class BrowserProfilePathGuard
{
  public static void Validate(string profileDirectory)
  {
    var protectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(Environment.CurrentDirectory)
        };
    AddRepositoryRoot(protectedRoots, AppContext.BaseDirectory);
    AddRepositoryRoot(protectedRoots, Environment.CurrentDirectory);

    if (protectedRoots.Any(root =>
            IsWithin(profileDirectory, root) || IsWithin(root, profileDirectory)))
    {
      throw new ArgumentException(
          "The browser profile must be stored outside the repository/application directory.",
          nameof(profileDirectory));
    }

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrWhiteSpace(localAppData))
    {
      var defaultEdgeProfile = Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
      if (IsWithin(profileDirectory, defaultEdgeProfile) || IsWithin(defaultEdgeProfile, profileDirectory))
      {
        throw new ArgumentException(
            "R-fbm may not use or contain the user's default Edge profile.",
            nameof(profileDirectory));
      }
    }
  }

  private static void AddRepositoryRoot(ISet<string> protectedRoots, string startDirectory)
  {
    var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
    while (current is not null)
    {
      if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
          File.Exists(Path.Combine(current.FullName, ".git")))
      {
        protectedRoots.Add(current.FullName);
        return;
      }
      current = current.Parent;
    }
  }

  private static bool IsWithin(string candidate, string parent)
  {
    var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
    return relative == "." ||
        (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
         !Path.IsPathRooted(relative));
  }
}
