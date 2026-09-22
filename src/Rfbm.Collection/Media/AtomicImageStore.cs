using System.Net;
using Rfbm.Collection.Contracts;

namespace Rfbm.Collection.Media;

public sealed record SavedImage(
    string FullPath,
    string RelativePath,
    string MediaType,
    long ByteLength);

public sealed class AtomicImageStore
{
  private static readonly IReadOnlyDictionary<string, string> Extensions =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/avif"] = ".avif"
      };

  private readonly string _mediaRoot;
  private readonly HttpClient _httpClient;
  private readonly long _maxBytes;

  public AtomicImageStore(string mediaRoot, HttpClient httpClient, long maxBytes = 12 * 1024 * 1024)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(mediaRoot);
    ArgumentNullException.ThrowIfNull(httpClient);
    if (maxBytes is < 1 or > 64 * 1024 * 1024)
    {
      throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }

    _mediaRoot = Path.GetFullPath(mediaRoot);
    _httpClient = httpClient;
    _maxBytes = maxBytes;
  }

  public async Task<SavedImage> SaveAsync(
      Uri uri,
      ThreadKey thread,
      int position,
      string captureScope,
      CancellationToken cancellationToken)
  {
    if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
    {
      throw new InvalidDataException("Image URL must be HTTP(S).");
    }
    if (IsPrivateDestination(uri))
    {
      throw new InvalidDataException("Image URL may not target a loopback or private network address.");
    }
    if (position is < 1 or > 9)
    {
      throw new ArgumentOutOfRangeException(nameof(position));
    }
    ArgumentException.ThrowIfNullOrWhiteSpace(captureScope);

    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
    using var response = await _httpClient.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    response.EnsureSuccessStatusCode();

    var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
    if (!Extensions.TryGetValue(mediaType, out var extension))
    {
      throw new InvalidDataException("Only supported image responses may be saved.");
    }
    if (response.Content.Headers.ContentLength is > 0 &&
        response.Content.Headers.ContentLength > _maxBytes)
    {
      throw new InvalidDataException("Image exceeds the configured byte limit.");
    }

    var sitePart = SafeSegment(thread.SiteFamily.Value);
    var threadPart = SafeSegment(thread.ThreadId);
    var scopePart = SafeSegment(captureScope);
    var directory = Path.Combine(_mediaRoot, sitePart, threadPart, scopePart);
    Directory.CreateDirectory(directory);
    var fileName = $"{position:000}{extension}";
    var finalPath = Path.Combine(directory, fileName);
    var temporaryPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");

    try
    {
      await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
      await using var target = new FileStream(
          temporaryPath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          81920,
          FileOptions.Asynchronous | FileOptions.WriteThrough);
      var buffer = new byte[81920];
      var signature = new byte[16];
      var signatureLength = 0;
      long total = 0;
      while (true)
      {
        var read = await source.ReadAsync(buffer, cancellationToken);
        if (read == 0)
        {
          break;
        }
        total += read;
        if (total > _maxBytes)
        {
          throw new InvalidDataException("Image exceeds the configured byte limit.");
        }
        if (signatureLength < signature.Length)
        {
          var signatureBytes = Math.Min(read, signature.Length - signatureLength);
          buffer.AsSpan(0, signatureBytes).CopyTo(signature.AsSpan(signatureLength));
          signatureLength += signatureBytes;
        }
        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
      }
      if (!HasExpectedSignature(mediaType, signature.AsSpan(0, signatureLength)))
      {
        throw new InvalidDataException("Image bytes do not match the declared media type.");
      }
      await target.FlushAsync(cancellationToken);
      target.Close();

      File.Move(temporaryPath, finalPath, overwrite: true);
      var relative = Path.GetRelativePath(_mediaRoot, finalPath);
      return new SavedImage(finalPath, relative, mediaType, total);
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private static string SafeSegment(string value)
  {
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var result = new string(value
        .Where(character => !invalid.Contains(character) && !char.IsControl(character))
        .Take(80)
        .ToArray());
    if (string.IsNullOrWhiteSpace(result) || result is "." or "..")
    {
      throw new InvalidDataException("Unsafe media path segment.");
    }
    return result;
  }

  private static bool IsPrivateDestination(Uri uri)
  {
    if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }
    if (!IPAddress.TryParse(uri.Host, out var address))
    {
      return false;
    }
    return !ImageNetworkPolicy.IsPublicAddress(address);
  }

  private static bool HasExpectedSignature(string mediaType, ReadOnlySpan<byte> bytes) =>
      mediaType.ToLowerInvariant() switch
      {
        "image/png" => bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
              bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
        "image/avif" => bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8) &&
              (bytes.Slice(8, 4).SequenceEqual("avif"u8) || bytes.Slice(8, 4).SequenceEqual("avis"u8)),
        _ => false
      };
}
