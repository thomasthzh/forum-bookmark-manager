using System.Net;
using System.Net.Sockets;

namespace Rfbm.Collection.Media;

public static class ImageNetworkPolicy
{
  public static bool IsPublicAddress(IPAddress address)
  {
    ArgumentNullException.ThrowIfNull(address);
    if (address.IsIPv4MappedToIPv6)
    {
      return IsPublicAddress(address.MapToIPv4());
    }

    if (address.AddressFamily == AddressFamily.InterNetworkV6)
    {
      if (address.Equals(IPAddress.IPv6Any) ||
          address.Equals(IPAddress.IPv6None) ||
          IPAddress.IsLoopback(address) ||
          address.IsIPv6LinkLocal ||
          address.IsIPv6SiteLocal ||
          address.IsIPv6Multicast)
      {
        return false;
      }

      var bytes = address.GetAddressBytes();
      if ((bytes[0] & 0xFE) == 0xFC)
      {
        return false;
      }

      return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }

    if (address.AddressFamily != AddressFamily.InterNetwork)
    {
      return false;
    }

    var octets = address.GetAddressBytes();
    return !(octets[0] == 0 ||
        octets[0] == 10 ||
        octets[0] == 100 && octets[1] is >= 64 and <= 127 ||
        octets[0] == 127 ||
        octets[0] == 169 && octets[1] == 254 ||
        octets[0] == 172 && octets[1] is >= 16 and <= 31 ||
        octets[0] == 192 && octets[1] == 0 && octets[2] is 0 or 2 ||
        octets[0] == 192 && octets[1] == 88 && octets[2] == 99 ||
        octets[0] == 192 && octets[1] == 168 ||
        octets[0] == 198 && octets[1] is 18 or 19 ||
        octets[0] == 198 && octets[1] == 51 && octets[2] == 100 ||
        octets[0] == 203 && octets[1] == 0 && octets[2] == 113 ||
        octets[0] >= 224);
  }
}

internal static class SafeImageHttpClient
{
  public static HttpClient Shared { get; } = Create();

  private static HttpClient Create()
  {
    var handler = new SocketsHttpHandler
    {
      AllowAutoRedirect = true,
      MaxAutomaticRedirections = 5,
      PooledConnectionLifetime = TimeSpan.FromMinutes(5),
      ConnectCallback = ConnectPublicAddressAsync
    };
    return new HttpClient(handler)
    {
      Timeout = TimeSpan.FromSeconds(45)
    };
  }

  private static async ValueTask<Stream> ConnectPublicAddressAsync(
      SocketsHttpConnectionContext context,
      CancellationToken cancellationToken)
  {
    var addresses = await Dns.GetHostAddressesAsync(
        context.DnsEndPoint.Host,
        cancellationToken);
    var publicAddresses = addresses.Where(ImageNetworkPolicy.IsPublicAddress).ToArray();
    if (publicAddresses.Length == 0)
    {
      throw new HttpRequestException("Image host did not resolve to a public network address.");
    }

    Exception? lastFailure = null;
    foreach (var address in publicAddresses)
    {
      var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
      {
        NoDelay = true
      };
      try
      {
        await socket.ConnectAsync(
            new IPEndPoint(address, context.DnsEndPoint.Port),
            cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
      }
      catch (Exception ex) when (ex is SocketException or IOException)
      {
        lastFailure = ex;
        socket.Dispose();
      }
    }

    throw new HttpRequestException("Image host could not be reached through a public address.", lastFailure);
  }
}
