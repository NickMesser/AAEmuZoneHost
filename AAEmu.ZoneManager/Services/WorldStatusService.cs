using System.IO;
using System.Net.Http;
using System.Text.Json;
using AAEmu.ZoneManager.Models;

namespace AAEmu.ZoneManager.Services;

internal sealed class WorldStatusService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public async Task<WorldStatusSnapshot> GetAsync(
        ZoneManagerSettings settings,
        CancellationToken cancellationToken)
    {
        var host = settings.WorldIp.Trim();
        if (host is "*" or "0.0.0.0")
            host = "127.0.0.1";

        var endpoint = new UriBuilder(
            Uri.UriSchemeHttp,
            host,
            settings.WorldApiPort,
            "api/world/zone-manager-status").Uri;
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<WorldStatusSnapshot>(body, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("World returned an empty status response.");
    }

    public void Dispose() => _httpClient.Dispose();
}
