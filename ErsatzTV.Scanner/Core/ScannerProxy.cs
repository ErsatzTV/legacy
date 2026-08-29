using System.Net.Http.Json;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Security;
using ErsatzTV.Scanner.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core;

public class ScannerProxy(IHttpClientFactory httpClientFactory, ILogger<ScannerProxy> logger) : IScannerProxy
{
    private string? _baseUrl;
    private Option<ApiSecrets> _secrets;

    public void SetBaseUrl(string baseUrl)
    {
        _baseUrl = baseUrl;

        if (File.Exists(FileSystemLayout.ApiSecretsPath))
        {
            string contents = File.ReadAllText(FileSystemLayout.ApiSecretsPath);
            _secrets = Optional(JsonSerializer.Deserialize<ApiSecrets>(contents));
        }
    }

    public async Task<bool> UpdateProgress(decimal progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            return false;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            SetApiKey(httpClient);
            var url = $"{_baseUrl}/progress";
            var response = await httpClient.PostAsJsonAsync(url, progress, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Scanner failed to update progress");
        }

        return false;
    }

    public async Task<bool> ReindexMediaItems(int[] mediaItemIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            return false;
        }

        if (mediaItemIds.Length == 0)
        {
            return true;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            SetApiKey(httpClient);
            var url = $"{_baseUrl}/items/reindex";
            var response = await httpClient.PostAsJsonAsync(url, mediaItemIds, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Scanner failed to reindex media items");
        }

        return false;
    }

    public async Task<bool> RemoveMediaItems(int[] mediaItemIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            return false;
        }

        if (mediaItemIds.Length == 0)
        {
            return true;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            SetApiKey(httpClient);
            var url = $"{_baseUrl}/items/remove";
            var response = await httpClient.PostAsJsonAsync(url, mediaItemIds, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Scanner failed to remove media items");
        }

        return false;
    }

    private void SetApiKey(HttpClient httpClient)
    {
        foreach (ApiSecrets secrets in _secrets)
        {
            httpClient.DefaultRequestHeaders.Add(ApiHelper.HeaderName, secrets.ApiKey);
        }
    }
}
