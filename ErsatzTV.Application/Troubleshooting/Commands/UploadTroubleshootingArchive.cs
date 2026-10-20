using ErsatzTV.Core.Interfaces.Troubleshooting;

namespace ErsatzTV.Application.Troubleshooting;

public record UploadTroubleshootingArchive(string ArchivePath)
    : IRequest<Either<TroubleshootingUploadError, string>>;
