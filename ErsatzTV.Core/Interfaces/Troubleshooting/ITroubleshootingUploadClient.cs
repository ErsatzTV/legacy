namespace ErsatzTV.Core.Interfaces.Troubleshooting;

public record TroubleshootingUploadError(int? StatusCode, string Message);

public interface ITroubleshootingUploadClient
{
    Task<Either<TroubleshootingUploadError, string>> Upload(
        Stream ciphertext,
        string version,
        CancellationToken cancellationToken);
}
