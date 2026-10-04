using System.Text.Json;
namespace CinderAtlas;
public sealed record ChartSettings(string Caption, int Zoom);
public static class ChartCodec {
    public static string Encode(ChartSettings settings)
        => JsonSerializer.Serialize(settings);
}
