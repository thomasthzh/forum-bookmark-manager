namespace Rfbm.BrowserBridge.Contracts;

public interface IBrowserBridgeMessage
{
  string Type { get; }
  string Nonce { get; }
}
