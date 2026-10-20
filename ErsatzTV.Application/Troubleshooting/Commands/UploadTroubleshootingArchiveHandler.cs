using System.IO.Abstractions;
using System.Reflection;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Core.Troubleshooting;

namespace ErsatzTV.Application.Troubleshooting;

public class UploadTroubleshootingArchiveHandler(
    IAgeEncryptor ageEncryptor,
    ITroubleshootingUploadClient uploadClient,
    IFileSystem fileSystem)
    : IRequestHandler<UploadTroubleshootingArchive, Either<TroubleshootingUploadError, string>>
{
    private static readonly string Version =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public async Task<Either<TroubleshootingUploadError, string>> Handle(
        UploadTroubleshootingArchive request,
        CancellationToken cancellationToken)
    {
        if (!TroubleshootingUpload.IsArchivePath(request.ArchivePath) ||
            !fileSystem.File.Exists(request.ArchivePath))
        {
            return new TroubleshootingUploadError(null, "The prepared results are gone.");
        }

        using var ciphertext = new MemoryStream();
        await using (FileSystemStream archive = fileSystem.File.OpenRead(request.ArchivePath))
        {
            long expected = ageEncryptor.GetCiphertextLength(
                archive.Length,
                TroubleshootingUpload.Recipients.Length);

            if (expected > TroubleshootingUpload.MaxCiphertextLength)
            {
                return new TroubleshootingUploadError(null, "The results are too large to upload.");
            }

            ageEncryptor.Encrypt(archive, ciphertext, TroubleshootingUpload.Recipients);

            // file changed after the size check
            if (ciphertext.Length != expected)
            {
                return new TroubleshootingUploadError(null, "The results changed while they were encrypted.");
            }
        }

        ciphertext.Position = 0;
        Either<TroubleshootingUploadError, string> result = await uploadClient.Upload(
            ciphertext,
            Version,
            cancellationToken);

        if (result.IsRight)
        {
            fileSystem.File.Delete(request.ArchivePath);
        }

        return result;
    }
}
