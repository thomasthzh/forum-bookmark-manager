using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Rfbm.Collection.Contracts;

namespace Rfbm.Collection.Engine;

internal sealed class CollectionSession : ICollectionSession
{
  internal const int EventCapacity = 1024;

  private readonly object _gate = new();
  private readonly Queue<CollectionEvent> _events = new(EventCapacity);
  private readonly Channel<bool> _eventAvailable = Channel.CreateBounded<bool>(
      new BoundedChannelOptions(1)
      {
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false
      });
  private readonly Func<CancellationToken, ValueTask> _cancel;
  private FileStream? _overflow;
  private long _overflowReadPosition;
  private long _overflowWritePosition;
  private bool _eventsComplete;
  private bool _disposed;
  private long _nextSequence;
  private int _cancelRequested;

  public CollectionSession(CollectionRunId id, Func<CancellationToken, ValueTask> cancel)
  {
    Id = id;
    _cancel = cancel;
  }

  public CollectionRunId Id { get; }

  public Task<CollectionResult> Completion { get; internal set; } = null!;

  public async IAsyncEnumerable<CollectionEvent> Events(
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    while (true)
    {
      CollectionEvent? item;
      bool complete;
      lock (_gate)
      {
        item = TryTakeEvent();
        complete = item is null && _eventsComplete;
      }

      if (item is not null)
      {
        yield return item;
        continue;
      }

      if (complete ||
          !await _eventAvailable.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
      {
        yield break;
      }

      while (_eventAvailable.Reader.TryRead(out _))
      {
      }
    }
  }

  public async ValueTask RequestCancelAsync(CancellationToken cancellationToken = default)
  {
    if ((Completion is not null && Completion.IsCompleted) ||
        Interlocked.Exchange(ref _cancelRequested, 1) != 0)
    {
      return;
    }

    await _cancel(cancellationToken);
  }

  public ValueTask DisposeAsync()
  {
    FileStream? overflow;
    lock (_gate)
    {
      if (_disposed)
      {
        return ValueTask.CompletedTask;
      }

      _disposed = true;
      _eventsComplete = true;
      _events.Clear();
      overflow = _overflow;
      _overflow = null;
      _eventAvailable.Writer.TryComplete();
    }

    overflow?.Dispose();
    return ValueTask.CompletedTask;
  }

  internal void Publish(CollectionEvent item)
  {
    lock (_gate)
    {
      if (_eventsComplete)
      {
        return;
      }

      var sequenced = item with { Sequence = ++_nextSequence };
      if (sequenced is ProgressChanged &&
          (_events.Count >= EventCapacity || HasOverflow))
      {
        return;
      }

      if (_events.Count < EventCapacity && !HasOverflow)
      {
        _events.Enqueue(sequenced);
      }
      else
      {
        WriteOverflow(sequenced);
      }

      _eventAvailable.Writer.TryWrite(true);
    }
  }

  internal void CompleteEvents()
  {
    lock (_gate)
    {
      if (_eventsComplete)
      {
        return;
      }

      _eventsComplete = true;
      _eventAvailable.Writer.TryComplete();
    }
  }

  private bool HasOverflow => _overflowReadPosition < _overflowWritePosition;

  private CollectionEvent? TryTakeEvent()
  {
    if (_events.Count > 0)
    {
      return _events.Dequeue();
    }

    if (!HasOverflow)
    {
      return null;
    }

    var item = ReadOverflow();
    if (_overflowReadPosition == _overflowWritePosition)
    {
      _overflow!.Dispose();
      _overflow = null;
      _overflowReadPosition = 0;
      _overflowWritePosition = 0;
    }

    return item;
  }

  private void WriteOverflow(CollectionEvent item)
  {
    _overflow ??= new FileStream(
        Path.Combine(Path.GetTempPath(), $"rfbm-events-{Id}-{Guid.NewGuid():N}.tmp"),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.Read | FileShare.Delete,
        bufferSize: 4096,
        FileOptions.DeleteOnClose | FileOptions.SequentialScan);

    var kind = item switch
    {
      StateChanged => (byte)1,
      ProgressChanged => (byte)2,
      ItemFailed => (byte)3,
      _ => throw new InvalidOperationException($"Unsupported collection event type '{item.GetType().Name}'.")
    };
    var payload = JsonSerializer.SerializeToUtf8Bytes(item, item.GetType());
    Span<byte> header = stackalloc byte[5];
    header[0] = kind;
    BinaryPrimitives.WriteInt32LittleEndian(header[1..], payload.Length);

    _overflow.Position = _overflowWritePosition;
    _overflow.Write(header);
    _overflow.Write(payload);
    _overflowWritePosition = _overflow.Position;
  }

  private CollectionEvent ReadOverflow()
  {
    _overflow!.Flush();
    _overflow.Position = _overflowReadPosition;
    Span<byte> header = stackalloc byte[5];
    _overflow.ReadExactly(header);
    var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[1..]);
    if (payloadLength < 0)
    {
      throw new InvalidDataException("Collection event overflow record has an invalid length.");
    }

    var payload = GC.AllocateUninitializedArray<byte>(payloadLength);
    _overflow.ReadExactly(payload);
    _overflowReadPosition = _overflow.Position;

    return header[0] switch
    {
      1 => JsonSerializer.Deserialize<StateChanged>(payload)!,
      2 => JsonSerializer.Deserialize<ProgressChanged>(payload)!,
      3 => JsonSerializer.Deserialize<ItemFailed>(payload)!,
      _ => throw new InvalidDataException("Collection event overflow record has an invalid type.")
    };
  }
}
