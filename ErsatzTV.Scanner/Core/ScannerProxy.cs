using System.Net;
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
    private bool _loggedKeyRejection;
    private Option<ApiSecrets> _secrets;

    public void SetBaseUrl(string baseUrl)
    {
        _baseUrl = baseUrl;

        if (!File.Exists(FileSystemLayout.ApiSecretsPath))
        {
            logger.LogWarning(
                "Api secrets file {Path} does not exist; scanner requests will be rejected",
                FileSystemLayout.ApiSecretsPath);
            return;
        }

        try
        {
            string contents = File.ReadAllText(FileSystemLayout.ApiSecretsPath);
            _secrets = Optional(JsonSerializer.Deserialize<ApiSecrets>(contents));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to read api secrets file {Path}; scanner requests will be rejected",
                FileSystemLayout.ApiSecretsPath);
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
            return LogUnsuccessfulResponse(response, "update progress");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scanner failed to update progress");
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
            return LogUnsuccessfulResponse(response, "reindex media items");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scanner failed to reindex media items");
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
            return LogUnsuccessfulResponse(response, "remove media items");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scanner failed to remove media items");
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

    private bool LogUnsuccessfulResponse(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // some callers continue after a failure, so warn one time for the whole scan
            if (!_loggedKeyRejection)
            {
                _loggedKeyRejection = true;
                logger.LogWarning(
                    "Scanner failed to {Action}; ErsatzTV rejected the api key from {Path}",
                    action,
                    FileSystemLayout.ApiSecretsPath);
            }
        }
        else
        {
            logger.LogWarning(
                "Scanner failed to {Action}; ErsatzTV returned {StatusCode}",
                action,
                (int)response.StatusCode);
        }

        return false;
    }
}
