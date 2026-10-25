using System.Text.Json;

namespace ControllerClient.Services;

internal static class DebugOptions
{
    internal const string FileName = "can-pilot.debug.json";

    public static bool ShowServerEndpoints => LoadShowServerEndpoints(Path.Combine(AppContext.BaseDirectory, FileName));

    internal static bool LoadShowServerEndpoints(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("show_server_endpoints", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
