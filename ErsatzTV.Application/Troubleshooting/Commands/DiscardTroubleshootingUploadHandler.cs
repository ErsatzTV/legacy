using System.IO.Abstractions;
using ErsatzTV.Core.Troubleshooting;

namespace ErsatzTV.Application.Troubleshooting;

public class DiscardTroubleshootingUploadHandler(IFileSystem fileSystem)
    : IRequestHandler<DiscardTroubleshootingUpload>
{
    public Task Handle(DiscardTroubleshootingUpload request, CancellationToken cancellationToken)
    {
        if (TroubleshootingUpload.IsArchivePath(request.ArchivePath))
        {
            try
            {
                fileSystem.File.Delete(request.ArchivePath);
            }
            catch (IOException)
            {
                // in use on Windows; startup deletes it
            }
        }

        return Task.CompletedTask;
    }
}
