using System.Text;

namespace Rfbm.Collection.Engine;

internal sealed class CollectionRunLease : IDisposable
{
  private readonly FileStream _stream;

  private CollectionRunLease(FileStream stream)
  {
    _stream = stream;
  }

  public static bool TryAcquire(string databasePath, out CollectionRunLease? lease)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
    var fullDatabasePath = Path.GetFullPath(databasePath);
    var directory = Path.GetDirectoryName(fullDatabasePath)
        ?? throw new ArgumentException("Database path has no parent directory.", nameof(databasePath));
    Directory.CreateDirectory(directory);
    var leasePath = fullDatabasePath + ".collection.lock";

    try
    {
      var stream = new FileStream(
          leasePath,
          FileMode.OpenOrCreate,
          FileAccess.ReadWrite,
          FileShare.None,
          bufferSize: 256,
          FileOptions.WriteThrough);
      var payload = Encoding.UTF8.GetBytes(
          $"pid={Environment.ProcessId};acquired={DateTimeOffset.UtcNow:O}");
      stream.SetLength(0);
      stream.Write(payload);
      stream.Flush(flushToDisk: true);
      lease = new CollectionRunLease(stream);
      return true;
    }
    catch (IOException)
    {
      lease = null;
      return false;
    }
  }

  public void Dispose() => _stream.Dispose();
}
