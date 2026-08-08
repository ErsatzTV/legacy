using System.IO.Compression;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Metadata;

namespace ErsatzTV.Application.Troubleshooting;

public class ArchiveTroubleshootingResultsHandler(ILocalFileSystem localFileSystem)
    : IRequestHandler<ArchiveTroubleshootingResults, Option<string>>
{
    public Task<Option<string>> Handle(ArchiveTroubleshootingResults request, CancellationToken cancellationToken)
    {
        string tempFile = Path.GetTempFileName();
        using ZipArchive zipArchive = ZipFile.Open(tempFile, ZipArchiveMode.Update);

        var hasReport = false;
        List<string> directories =
        [
            FileSystemLayout.TranscodeTroubleshootingFolder,
            .. localFileSystem.ListSubdirectories(FileSystemLayout.TranscodeTroubleshootingFolder)
        ];

        foreach (string directory in directories)
        {
            foreach (string file in localFileSystem.ListFiles(directory))
            {
                string fileName = Path.GetFileName(file);

                // add to archive
                if (fileName.StartsWith("ffmpeg-", StringComparison.OrdinalIgnoreCase))
                {
                    hasReport = true;
                    zipArchive.CreateEntryFromFile(file, fileName);
                    continue;
                }

                if (fileName.Equals("logs.txt", StringComparison.OrdinalIgnoreCase))
                {
                    zipArchive.CreateEntryFromFile(file, fileName);
                    continue;
                }

                if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    zipArchive.CreateEntryFromFile(file, fileName);
                    continue;
                }

                if (fileName.Contains("capabilities", StringComparison.OrdinalIgnoreCase))
                {
                    zipArchive.CreateEntryFromFile(file, fileName);
                    continue;
                }

                if (fileName.Contains("stream-selector", StringComparison.OrdinalIgnoreCase))
                {
                    zipArchive.CreateEntryFromFile(file, fileName);
                    continue;
                }

                if (fileName.Contains("ffreport", StringComparison.OrdinalIgnoreCase))
                {
                    hasReport = true;
                    zipArchive.CreateEntryFromFile(file, fileName);
                    continue;
                }

                if (fileName.Contains("outcome", StringComparison.OrdinalIgnoreCase))
                {
                    zipArchive.CreateEntryFromFile(file, fileName);
                }
            }
        }

        return Task.FromResult(hasReport ? tempFile : Option<string>.None);
    }
}
