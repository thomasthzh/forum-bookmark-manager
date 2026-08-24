using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Rfbm.Collection.Network;

public sealed record NetworkFailureContext(Uri Uri, string SafeMessage);

public sealed record NetworkRecoveryResult(bool Changed, string SafeMessage);

public interface INetworkRecoveryAdapter
{
  ValueTask<NetworkRecoveryResult> TryRecoverAsync(
      NetworkFailureContext failure,
      CancellationToken cancellationToken);
}

public sealed class MihomoNetworkRecoveryAdapter : INetworkRecoveryAdapter
{
  private readonly HttpClient _httpClient;
  private readonly string? _group;
  private readonly string? _proxy;

  public MihomoNetworkRecoveryAdapter(
      HttpClient httpClient,
      string? controllerSecret = null,
      string? group = null,
      string? proxy = null)
  {
    ArgumentNullException.ThrowIfNull(httpClient);
    if (httpClient.BaseAddress is null ||
        !httpClient.BaseAddress.IsAbsoluteUri ||
        !System.Net.IPAddress.TryParse(httpClient.BaseAddress.Host, out var address) ||
        !System.Net.IPAddress.IsLoopback(address))
    {
      throw new ArgumentException("Mihomo controller must use an explicit loopback IP address.", nameof(httpClient));
    }

    _httpClient = httpClient;
    _group = group;
    _proxy = proxy;
    if (!string.IsNullOrWhiteSpace(controllerSecret))
    {
      _httpClient.DefaultRequestHeaders.Authorization =
          new AuthenticationHeaderValue("Bearer", controllerSecret);
    }
  }

  public async ValueTask<NetworkRecoveryResult> TryRecoverAsync(
      NetworkFailureContext failure,
      CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(_group) || string.IsNullOrWhiteSpace(_proxy))
    {
      return new NetworkRecoveryResult(false, "未配置 Mihomo 线路切换。");
    }

    return await SelectProxyAsync(_group, _proxy, cancellationToken);
  }

  public async Task<NetworkRecoveryResult> SelectProxyAsync(
      string group,
      string proxy,
      CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(group);
    ArgumentException.ThrowIfNullOrWhiteSpace(proxy);

    var path = $"/proxies/{Uri.EscapeDataString(group)}";
    using var response = await _httpClient.PutAsJsonAsync(
        path,
        new { name = proxy },
        cancellationToken);
    response.EnsureSuccessStatusCode();
    return new NetworkRecoveryResult(true, "已请求本机 Mihomo 切换线路。");
  }
}
