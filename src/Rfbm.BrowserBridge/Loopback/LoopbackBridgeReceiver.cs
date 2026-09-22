using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rfbm.BrowserBridge.Contracts;

namespace Rfbm.BrowserBridge.Loopback;

public sealed class LoopbackBridgeReceiver : IAsyncDisposable
{
  public const string SessionPath = "/api/extension/session";
  public const string PageSnapshotPath = "/api/extension/page-snapshot";
  public const string SelectorProfilePath = "/api/extension/selector-profile";

  private readonly WebApplication application;
  private readonly Func<IBrowserBridgeMessage, CancellationToken, ValueTask> onMessage;
  private readonly HashSet<string> allowedOrigins;
  private readonly int maxPayloadBytes;
  private readonly CancellationTokenSource shutdown = new();
  private int disposed;

  private LoopbackBridgeReceiver(
      WebApplication application,
      Func<IBrowserBridgeMessage, CancellationToken, ValueTask> onMessage,
      LoopbackBridgeReceiverOptions options)
  {
    this.application = application;
    this.onMessage = onMessage;
    allowedOrigins = new HashSet<string>(options.AllowedOrigins, StringComparer.Ordinal);
    maxPayloadBytes = options.MaxPayloadBytes;
    SessionNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
  }

  public string SessionNonce { get; }
  public Uri EndpointUri { get; private set; } = null!;
  public Uri SessionEndpointUri => new(EndpointUri, SessionPath);
  public Uri PageSnapshotEndpointUri => new(EndpointUri, PageSnapshotPath);
  public Uri SelectorProfileEndpointUri => new(EndpointUri, SelectorProfilePath);

  public static async Task<LoopbackBridgeReceiver> StartAsync(
      Func<IBrowserBridgeMessage, CancellationToken, ValueTask> onMessage,
      LoopbackBridgeReceiverOptions? options = null,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(onMessage);
    options ??= new LoopbackBridgeReceiverOptions();
    ValidateOptions(options);

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, options.Port));

    var application = builder.Build();
    var receiver = new LoopbackBridgeReceiver(application, onMessage, options);
    application.Run(receiver.HandleAsync);

    try
    {
      await application.StartAsync(cancellationToken).ConfigureAwait(false);
      var server = application.Services.GetRequiredService<IServer>();
      var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
      var address = addresses?.SingleOrDefault()
          ?? throw new InvalidOperationException("The loopback receiver did not publish an address.");
      receiver.EndpointUri = new Uri(address.EndsWith('/') ? address : address + "/");
      return receiver;
    }
    catch
    {
      await application.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref disposed, 1) != 0)
    {
      return;
    }

    shutdown.Cancel();
    try
    {
      await application.StopAsync().ConfigureAwait(false);
    }
    finally
    {
      await application.DisposeAsync().ConfigureAwait(false);
      shutdown.Dispose();
    }
  }

  private async Task HandleAsync(HttpContext context)
  {
    using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted,
        shutdown.Token);
    var cancellationToken = requestLifetime.Token;

    if (!HasExpectedHost(context.Request.Host))
    {
      await WriteReplyAsync(context, StatusCodes.Status400BadRequest, false, "invalid_host", "The Host header is not accepted.");
      return;
    }

    var path = context.Request.Path.Value;
    if (path != SessionPath && path != PageSnapshotPath && path != SelectorProfilePath)
    {
      await WriteReplyAsync(context, StatusCodes.Status404NotFound, false, "route_not_found", "The bridge route does not exist.");
      return;
    }

    if (!IsAllowedOrigin(context.Request.Headers.Origin.ToString()))
    {
      await WriteReplyAsync(context, StatusCodes.Status403Forbidden, false, "origin_not_allowed", "The extension origin is not allowed.");
      return;
    }

    if (path == SessionPath)
    {
      if (!HttpMethods.IsGet(context.Request.Method))
      {
        await WriteReplyAsync(context, StatusCodes.Status405MethodNotAllowed, false, "method_not_allowed", "This route accepts GET only.");
        return;
      }

      await WriteSessionAsync(context);
      return;
    }

    if (!HttpMethods.IsPost(context.Request.Method))
    {
      await WriteReplyAsync(context, StatusCodes.Status405MethodNotAllowed, false, "method_not_allowed", "This route accepts POST only.");
      return;
    }

    if (!context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
    {
      await WriteReplyAsync(context, StatusCodes.Status415UnsupportedMediaType, false, "unsupported_media_type", "Content-Type must be application/json.");
      return;
    }

    var body = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
    if (body is null)
    {
      await WriteReplyAsync(context, StatusCodes.Status413PayloadTooLarge, false, "payload_too_large", "The bridge payload exceeds the configured limit.");
      return;
    }

    IBrowserBridgeMessage message;
    try
    {
      message = path == PageSnapshotPath
          ? JsonSerializer.Deserialize<PageSnapshot>(body, BridgeJson.Options)
              ?? throw new JsonException("The snapshot payload is empty.")
          : JsonSerializer.Deserialize<SelectorProfileMessage>(body, BridgeJson.Options)
              ?? throw new JsonException("The selector profile payload is empty.");
    }
    catch (JsonException)
    {
      await WriteReplyAsync(context, StatusCodes.Status400BadRequest, false, "invalid_json", "The request body is not a valid bridge message.");
      return;
    }

    if (!FixedTimeEquals(message.Nonce, SessionNonce))
    {
      await WriteReplyAsync(context, StatusCodes.Status401Unauthorized, false, "invalid_nonce", "The bridge session nonce is invalid.");
      return;
    }

    if (message is PageSnapshot snapshot && !snapshot.IsValid())
    {
      await WriteReplyAsync(context, StatusCodes.Status422UnprocessableEntity, false, "invalid_snapshot", "The page snapshot failed integrity validation.");
      return;
    }

    if (message is SelectorProfileMessage profile && !profile.IsValid())
    {
      await WriteReplyAsync(context, StatusCodes.Status422UnprocessableEntity, false, "invalid_selector_profile", "The selector profile is invalid.");
      return;
    }

    try
    {
      await onMessage(message, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      return;
    }
    catch
    {
      await WriteReplyAsync(context, StatusCodes.Status500InternalServerError, false, "delivery_failed", "The native application could not accept the message.");
      return;
    }

    await WriteReplyAsync(context, StatusCodes.Status202Accepted, true, "accepted", "The bridge message was accepted.");
  }

  private bool HasExpectedHost(HostString host)
  {
    return EndpointUri is not null &&
           string.Equals(host.Host, EndpointUri.Host, StringComparison.OrdinalIgnoreCase) &&
           host.Port == EndpointUri.Port;
  }

  private bool IsAllowedOrigin(string origin)
  {
    return !string.IsNullOrWhiteSpace(origin) && allowedOrigins.Contains(origin);
  }

  private async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
  {
    if (request.ContentLength > maxPayloadBytes)
    {
      return null;
    }

    using var buffer = new MemoryStream(Math.Min(maxPayloadBytes, 64 * 1024));
    var chunk = new byte[16 * 1024];
    while (true)
    {
      var read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
      if (read == 0)
      {
        return buffer.ToArray();
      }

      if (buffer.Length + read > maxPayloadBytes)
      {
        return null;
      }

      await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
    }
  }

  private async Task WriteSessionAsync(HttpContext context)
  {
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "application/json; charset=utf-8";
    await JsonSerializer.SerializeAsync(
        context.Response.Body,
        new
        {
          ok = true,
          code = "session",
          message = "Bridge session ready.",
          nonce = SessionNonce,
          pageSnapshotPath = PageSnapshotPath,
          selectorProfilePath = SelectorProfilePath,
          maxPayloadBytes
        },
        BridgeJson.Options,
        context.RequestAborted).ConfigureAwait(false);
  }

  private static async Task WriteReplyAsync(
      HttpContext context,
      int statusCode,
      bool ok,
      string code,
      string message)
  {
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "application/json; charset=utf-8";
    await JsonSerializer.SerializeAsync(
        context.Response.Body,
        new { ok, code, message },
        BridgeJson.Options,
        context.RequestAborted).ConfigureAwait(false);
  }

  private static bool FixedTimeEquals(string? provided, string expected)
  {
    if (provided is null)
    {
      return false;
    }

    var left = System.Text.Encoding.UTF8.GetBytes(provided);
    var right = System.Text.Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
  }

  private static void ValidateOptions(LoopbackBridgeReceiverOptions options)
  {
    if (options.Port is < 0 or > ushort.MaxValue)
    {
      throw new ArgumentOutOfRangeException(nameof(options.Port));
    }

    if (options.MaxPayloadBytes <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(options.MaxPayloadBytes));
    }

    if (options.AllowedOrigins.Count == 0 || options.AllowedOrigins.Any(string.IsNullOrWhiteSpace))
    {
      throw new ArgumentException("At least one non-empty extension origin is required.", nameof(options.AllowedOrigins));
    }
  }
}
