using System.Security.Cryptography;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Security;

namespace ErsatzTV.Services.RunOnce;

public class CreateApiKeyService(ILogger<CreateApiKeyService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            // first validate secrets
            var valid = false;
            if (File.Exists(FileSystemLayout.ApiSecretsPath))
            {
                try
                {
                    string contents = await File.ReadAllTextAsync(FileSystemLayout.ApiSecretsPath, stoppingToken);
                    Option<ApiSecrets> maybeSecrets = Optional(JsonSerializer.Deserialize<ApiSecrets>(contents));
                    foreach (var secrets in maybeSecrets)
                    {
                        valid = !string.IsNullOrWhiteSpace(secrets.ApiKey);
                        if (valid)
                        {
                            ApiHelper.ApiKey = secrets.ApiKey;
                        }
                    }
                }
                catch (Exception)
                {
                    // do not mark valid
                }

                if (!valid)
                {
                    logger.LogWarning("Deleting invalid API secrets file");
                    File.Delete(FileSystemLayout.ApiSecretsPath);
                }
            }

            // generate new secrets if needed
            if (!valid)
            {
                byte[] bytes = RandomNumberGenerator.GetBytes(32);
                string base64 = Convert.ToBase64String(bytes)
                    .TrimEnd('=')
                    .Replace("/", "_")
                    .Replace("+", "-");

                var secrets = new ApiSecrets
                {
                    ApiKey = base64
                };

                string contents = JsonSerializer.Serialize(secrets);
                await File.WriteAllTextAsync(FileSystemLayout.ApiSecretsPath, contents, stoppingToken);

                ApiHelper.ApiKey = secrets.ApiKey;
                logger.LogInformation("Created new API key");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create API key");
        }
    }
}
