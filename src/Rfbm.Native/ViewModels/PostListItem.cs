using System.ComponentModel;
using System.Runtime.CompilerServices;
using Rfbm.Core.Models;
using Rfbm.Data.Storage;

namespace Rfbm.Native.ViewModels;

public sealed class PostListItem : INotifyPropertyChanged
{
  private bool _isSelected;
  private PostStatus _status;
  private bool _isStarred;
  private string _note;
  private IReadOnlyList<string> _tags;

  public PostListItem(StoredPost post)
  {
    Key = post.Key;
    Title = post.Title;
    Type = post.ProjectType;
    Url = post.CanonicalUrl;
    _status = post.Status;
    FavoriteTime = post.FavoriteTimeUtc;
    FavoriteTimeText = post.FavoriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        ?? post.FavoriteTimeRaw
        ?? "-";
    _isStarred = post.IsStarred;
    _note = post.Note;
    _tags = post.Tags;
    BodyText = post.BodyText ?? string.Empty;
    ExtractPassword = post.ExtractPassword ?? string.Empty;
    Images = post.Images;
    ResourceLinks = post.ResourceLinks;
    DownloadCount = post.DownloadCount;
    VisitCount = post.VisitCount;
    FavoriteCount = post.FavoriteCount;
    IsDeleted = post.DeletedAt is not null;
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  public PostKey Key { get; }
  public string Id => Key.ThreadId;
  public string Title { get; }
  public string Type { get; }
  public string Url { get; }
  public DateTimeOffset? FavoriteTime { get; }
  public string FavoriteTimeText { get; }
  public string BodyText { get; }
  public string ExtractPassword { get; }
  public IReadOnlyList<CapturedPostImage> Images { get; }
  public IReadOnlyList<CapturedResourceLink> ResourceLinks { get; }
  public int? DownloadCount { get; }
  public int? VisitCount { get; }
  public int? FavoriteCount { get; }
  public bool IsDeleted { get; }
  public bool HasPassword => !string.IsNullOrWhiteSpace(ExtractPassword);
  public bool HasImages => Images.Count > 0;
  public bool HasLinks => ResourceLinks.Count > 0;

  public bool IsSelected
  {
    get => _isSelected;
    set => SetField(ref _isSelected, value);
  }

  public PostStatus Status
  {
    get => _status;
    internal set
    {
      if (SetField(ref _status, value))
      {
        OnPropertyChanged(nameof(StatusText));
      }
    }
  }

  public string StatusText => Status.DisplayName();

  public bool IsStarred
  {
    get => _isStarred;
    internal set
    {
      if (SetField(ref _isStarred, value))
      {
        OnPropertyChanged(nameof(StarText));
      }
    }
  }

  public string StarText => IsStarred ? "★" : "☆";

  public string Note
  {
    get => _note;
    internal set => SetField(ref _note, value);
  }

  public IReadOnlyList<string> Tags
  {
    get => _tags;
    internal set
    {
      if (!ReferenceEquals(_tags, value))
      {
        _tags = value;
        OnPropertyChanged();
        OnPropertyChanged(nameof(TagsText));
      }
    }
  }

  public string TagsText => string.Join(" · ", Tags);

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
