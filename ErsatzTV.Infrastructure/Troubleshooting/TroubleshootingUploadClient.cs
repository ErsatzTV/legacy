using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Core.Troubleshooting;

namespace ErsatzTV.Infrastructure.Troubleshooting;

// no retry; a lost response may follow a stored upload
public class TroubleshootingUploadClient(IHttpClientFactory httpClientFactory) : ITroubleshootingUploadClient
{
    public const string HttpClientName = "TroubleshootingUpload";

    public async Task<Either<TroubleshootingUploadError, string>> Upload(
        Stream ciphertext,
        string version,
        CancellationToken cancellationToken)
    {
        long length = ciphertext.Length - ciphertext.Position;

        using var request = new HttpRequestMessage(HttpMethod.Post, TroubleshootingUpload.Url);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(
            "Upload-Metadata",
            $"version {Base64(version)},keys {Base64(TroubleshootingUpload.KeySet)}");

        request.Content = new StreamContent(ciphertext);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        request.Content.Headers.ContentLength = length;

        try
        {
            using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.Created)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new TroubleshootingUploadError((int)response.StatusCode, Truncate(body.Trim()));
            }

            string id = response.Headers.Location?.OriginalString.Split('/').LastOrDefault();
            if (!TroubleshootingUpload.IsValidId(id))
            {
                return new TroubleshootingUploadError(null, "The upload server returned an invalid ID.");
            }

            if (!response.Headers.TryGetValues("Upload-Offset", out IEnumerable<string> offsets) ||
                offsets.FirstOrDefault() != length.ToString(CultureInfo.InvariantCulture))
            {
                return new TroubleshootingUploadError(null, "The upload server did not store the whole file.");
            }

            return id;
        }
        catch (HttpRequestException ex)
        {
            return new TroubleshootingUploadError(null, $"Could not reach the upload server: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TroubleshootingUploadError(null, "The upload timed out.");
        }
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Truncate(string value) => value.Length > 500 ? value[..500] : value;
}
