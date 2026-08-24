namespace Rfbm.BrowserBridge.Loopback;

public sealed class LoopbackBridgeReceiverOptions
{
  public int Port { get; init; } = 53112;
  public int MaxPayloadBytes { get; init; } = 2_500_000;
  public IReadOnlyCollection<string> AllowedOrigins { get; init; } = Array.Empty<string>();
}
