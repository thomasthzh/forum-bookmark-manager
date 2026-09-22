using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Rfbm.BrowserBridge.Contracts;
using Rfbm.BrowserBridge.Loopback;
using Rfbm.Tests;

var fixedCaptureTime = new DateTimeOffset(2026, 8, 24, 8, 30, 0, TimeSpan.Zero);
const string html = "<html><body><h1>测试</h1></body></html>";

var snapshot = PageSnapshot.Create(
    nonce: "session-nonce",
    pageKind: PageKind.Thread,
    requestedUrl: "https://forum.example.test/thread-1?from=favorites",
    finalUrl: "https://mirror.example.test/thread-1",
    title: "  测试标题  ",
    html: html,
    runId: "run-1",
    workItemId: "work-1",
    ticket: "ticket-1",
    capturedAtUtc: fixedCaptureTime);

TestAssert.Equal("page_snapshot", snapshot.Type);
TestAssert.Equal(PageKind.Thread, snapshot.PageKind);
TestAssert.Equal("run-1", snapshot.RunId);
TestAssert.Equal("work-1", snapshot.WorkItemId);
TestAssert.Equal("ticket-1", snapshot.Ticket);
TestAssert.Equal(new Uri("https://forum.example.test/thread-1?from=favorites"), snapshot.RequestedUrl);
TestAssert.Equal(new Uri("https://mirror.example.test/thread-1"), snapshot.FinalUrl);
TestAssert.Equal("测试标题", snapshot.Title);
TestAssert.Equal(html, snapshot.Html);
TestAssert.Equal(Encoding.UTF8.GetByteCount(html), snapshot.HtmlByteLength);
TestAssert.Equal("b31d02cd18dc77376d50774e4281616000bbb3fc9edd8a5a9b4cce86c00d8d6b", snapshot.HtmlSha256);
TestAssert.Equal(fixedCaptureTime, snapshot.CapturedAtUtc);

TestAssert.Throws<ArgumentException>(() => PageSnapshot.Create(
    "nonce", PageKind.Thread, "", "https://example.test", "标题", html));
TestAssert.Throws<ArgumentException>(() => PageSnapshot.Create(
    "nonce", PageKind.Thread, "https://example.test", "file:///C:/temp/a.html", "标题", html));

var selectorProfile = SelectorProfileMessage.Create(
    nonce: "session-nonce",
    siteKey: "forum.example.test",
    sourceUrl: "https://forum.example.test/thread-1",
    selectors: new Dictionary<string, string>
    {
      ["threadTitle"] = " #thread-title ",
      ["postBody"] = "article[data-post-id]"
    },
    ticket: "ticket-selector",
    capturedAtUtc: fixedCaptureTime);

TestAssert.Equal("selector_profile", selectorProfile.Type);
TestAssert.Equal("#thread-title", selectorProfile.Selectors["threadTitle"]);
TestAssert.Equal("article[data-post-id]", selectorProfile.Selectors["postBody"]);
TestAssert.Throws<ArgumentException>(() => SelectorProfileMessage.Create(
    "nonce", "forum.example.test", "https://forum.example.test", new Dictionary<string, string> { ["unknown"] = ".x" }));

var acceptedMessages = new ConcurrentQueue<IBrowserBridgeMessage>();
var receiverOptions = new LoopbackBridgeReceiverOptions
{
  Port = 0,
  AllowedOrigins = ["chrome-extension://rfbm-test-extension"],
  MaxPayloadBytes = 8 * 1024
};

await using var receiver = await LoopbackBridgeReceiver.StartAsync(
    (message, _) =>
    {
      acceptedMessages.Enqueue(message);
      return ValueTask.CompletedTask;
    },
    receiverOptions);

TestAssert.Equal(true, IPAddress.IsLoopback(IPAddress.Parse(receiver.EndpointUri.Host)));
TestAssert.Equal(64, receiver.SessionNonce.Length);
TestAssert.Equal(true, receiver.EndpointUri.Port > 0);

using var client = new HttpClient();
var sessionResponse = await SendAsync(
    client,
    receiver.SessionEndpointUri,
    HttpMethod.Get,
    origin: "chrome-extension://rfbm-test-extension");
await AssertReplyAsync(sessionResponse, HttpStatusCode.OK, "session");
var sessionDocument = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
TestAssert.Equal(receiver.SessionNonce, sessionDocument.RootElement.GetProperty("nonce").GetString());

var acceptedSnapshot = snapshot with { Nonce = receiver.SessionNonce };
var acceptedResponse = await SendJsonAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    acceptedSnapshot,
    origin: "chrome-extension://rfbm-test-extension",
    configure: request =>
    {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "must-not-be-persisted");
      request.Headers.Add("Cookie", "session=must-not-be-persisted");
    });
await AssertReplyAsync(acceptedResponse, HttpStatusCode.Accepted, "accepted");
TestAssert.Equal(1, acceptedMessages.Count);
var receivedSnapshot = (PageSnapshot)acceptedMessages.Single();
TestAssert.Equal(html, receivedSnapshot.Html);
TestAssert.Equal(false, receivedSnapshot.GetType().GetProperties().Any(property =>
    property.Name.Contains("Cookie", StringComparison.OrdinalIgnoreCase) ||
    property.Name.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
    property.Name.Contains("Header", StringComparison.OrdinalIgnoreCase)));

var acceptedSelector = selectorProfile with { Nonce = receiver.SessionNonce };
var selectorResponse = await SendJsonAsync(
    client,
    receiver.SelectorProfileEndpointUri,
    acceptedSelector,
    origin: "chrome-extension://rfbm-test-extension");
await AssertReplyAsync(selectorResponse, HttpStatusCode.Accepted, "accepted");
TestAssert.Equal(2, acceptedMessages.Count);
TestAssert.Equal(true, acceptedMessages.Last() is SelectorProfileMessage);

var invalidNonceResponse = await SendJsonAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    acceptedSnapshot with { Nonce = "wrong-nonce" },
    origin: "chrome-extension://rfbm-test-extension");
await AssertReplyAsync(invalidNonceResponse, HttpStatusCode.Unauthorized, "invalid_nonce");

var wrongOriginResponse = await SendJsonAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    acceptedSnapshot,
    origin: "https://attacker.example.test");
await AssertReplyAsync(wrongOriginResponse, HttpStatusCode.Forbidden, "origin_not_allowed");

var missingOriginResponse = await SendJsonAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    acceptedSnapshot,
    origin: null);
await AssertReplyAsync(missingOriginResponse, HttpStatusCode.Forbidden, "origin_not_allowed");

var wrongHostResponse = await SendJsonAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    acceptedSnapshot,
    origin: "chrome-extension://rfbm-test-extension",
    configure: request => request.Headers.Host = "attacker.example.test");
await AssertReplyAsync(wrongHostResponse, HttpStatusCode.BadRequest, "invalid_host");

var wrongMethodResponse = await SendAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    HttpMethod.Get,
    origin: "chrome-extension://rfbm-test-extension");
await AssertReplyAsync(wrongMethodResponse, HttpStatusCode.MethodNotAllowed, "method_not_allowed");

var wrongPathResponse = await SendJsonAsync(
    client,
    new Uri(receiver.EndpointUri, "/api/extension/not-a-route"),
    acceptedSnapshot,
    origin: "chrome-extension://rfbm-test-extension");
await AssertReplyAsync(wrongPathResponse, HttpStatusCode.NotFound, "route_not_found");

var wrongContentTypeResponse = await SendAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    HttpMethod.Post,
    origin: "chrome-extension://rfbm-test-extension",
    content: new StringContent("{}", Encoding.UTF8, "text/plain"));
await AssertReplyAsync(wrongContentTypeResponse, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

var oversizedResponse = await SendAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    HttpMethod.Post,
    origin: "chrome-extension://rfbm-test-extension",
    content: new StringContent(new string('x', receiverOptions.MaxPayloadBytes + 1), Encoding.UTF8, "application/json"));
await AssertReplyAsync(oversizedResponse, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");

var tamperedSnapshot = acceptedSnapshot with { Html = acceptedSnapshot.Html + "tampered" };
var tamperedResponse = await SendJsonAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    tamperedSnapshot,
    origin: "chrome-extension://rfbm-test-extension");
await AssertReplyAsync(tamperedResponse, HttpStatusCode.UnprocessableEntity, "invalid_snapshot");

var malformedResponse = await SendAsync(
    client,
    receiver.PageSnapshotEndpointUri,
    HttpMethod.Post,
    origin: "chrome-extension://rfbm-test-extension",
    content: new StringContent("{not-json", Encoding.UTF8, "application/json"));
await AssertReplyAsync(malformedResponse, HttpStatusCode.BadRequest, "invalid_json");

TestAssert.Equal(2, acceptedMessages.Count);

var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var callbackCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var blockingReceiver = await LoopbackBridgeReceiver.StartAsync(
    async (_, cancellationToken) =>
    {
      callbackStarted.TrySetResult();
      try
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }
      catch (OperationCanceledException)
      {
        callbackCancelled.TrySetResult();
        throw;
      }
    },
    receiverOptions);
var blockingRequest = SendJsonAsync(
    client,
    blockingReceiver.PageSnapshotEndpointUri,
    snapshot with { Nonce = blockingReceiver.SessionNonce },
    origin: "chrome-extension://rfbm-test-extension");
await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
await blockingReceiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
await callbackCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
try
{
  await blockingRequest;
}
catch (HttpRequestException)
{
  // The server is allowed to close an in-flight request while shutting down.
}

Console.WriteLine("Rfbm.BrowserBridge.Tests passed.");

static Task<HttpResponseMessage> SendJsonAsync<T>(
    HttpClient client,
    Uri uri,
    T payload,
    string? origin,
    Action<HttpRequestMessage>? configure = null)
{
  return SendAsync(
      client,
      uri,
      HttpMethod.Post,
      origin,
      JsonContent.Create(payload, options: BridgeJson.Options),
      configure);
}

static Task<HttpResponseMessage> SendAsync(
    HttpClient client,
    Uri uri,
    HttpMethod method,
    string? origin,
    HttpContent? content = null,
    Action<HttpRequestMessage>? configure = null)
{
  var request = new HttpRequestMessage(method, uri) { Content = content };
  if (origin is not null)
  {
    request.Headers.TryAddWithoutValidation("Origin", origin);
  }

  configure?.Invoke(request);
  return client.SendAsync(request);
}

static async Task AssertReplyAsync(HttpResponseMessage response, HttpStatusCode status, string code)
{
  TestAssert.Equal(status, response.StatusCode, await response.Content.ReadAsStringAsync());
  TestAssert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
  var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  TestAssert.Equal(code, document.RootElement.GetProperty("code").GetString());
}
