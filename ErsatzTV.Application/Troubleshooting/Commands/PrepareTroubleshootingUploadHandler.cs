using System.IO.Abstractions;
using System.IO.Compression;
using ErsatzTV.Core;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Core.Troubleshooting;

namespace ErsatzTV.Application.Troubleshooting;

public class PrepareTroubleshootingUploadHandler(
    IMediator mediator,
    IEntityLocker entityLocker,
    ITempFilePool tempFilePool,
    IAgeEncryptor ageEncryptor,
    IFileSystem fileSystem)
    : IRequestHandler<PrepareTroubleshootingUpload, Either<BaseError, PreparedTroubleshootingUpload>>
{
    public async Task<Either<BaseError, PreparedTroubleshootingUpload>> Handle(
        PrepareTroubleshootingUpload request,
        CancellationToken cancellationToken)
    {
        if (entityLocker.IsTroubleshootingPlaybackLocked())
        {
            return BaseError.New("Troubleshooting playback is still running.");
        }

        string archivePath = tempFilePool.GetNextTempFile(TempFileCategory.TroubleshootingUpload);
        Option<string> maybeArchive = await mediator.Send(
            new ArchiveTroubleshootingResults(archivePath),
            cancellationToken);

        if (maybeArchive.IsNone)
        {
            return BaseError.New("There are no troubleshooting results to upload.");
        }

        List<PreparedTroubleshootingUploadEntry> entries;
        long archiveSize;
        await using (FileSystemStream stream = fileSystem.File.OpenRead(archivePath))
        {
            archiveSize = stream.Length;
            using var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
            entries = zipArchive.Entries
                .Map(e => new PreparedTroubleshootingUploadEntry(e.FullName, e.Length))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        long ciphertextLength = ageEncryptor.GetCiphertextLength(
            archiveSize,
            TroubleshootingUpload.Recipients.Length);

        return new PreparedTroubleshootingUpload(
            archivePath,
            entries,
            archiveSize,
            ciphertextLength > TroubleshootingUpload.MaxCiphertextLength);
    }
}
