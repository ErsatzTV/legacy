using ErsatzTV.Core;

namespace ErsatzTV.Application.Troubleshooting;

public record PrepareTroubleshootingUpload : IRequest<Either<BaseError, PreparedTroubleshootingUpload>>;

public record PreparedTroubleshootingUploadEntry(string Name, long Size);

public record PreparedTroubleshootingUpload(
    string ArchivePath,
    List<PreparedTroubleshootingUploadEntry> Entries,
    long ArchiveSize,
    bool TooLarge);
