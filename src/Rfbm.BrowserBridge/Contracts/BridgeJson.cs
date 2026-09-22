using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rfbm.BrowserBridge.Contracts;

public static class BridgeJson
{
  public static JsonSerializerOptions Options { get; } = CreateOptions();

  private static JsonSerializerOptions CreateOptions()
  {
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    return options;
  }
}
