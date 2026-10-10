using System.IO.Abstractions;
using System.IO.Compression;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Metadata;

namespace ErsatzTV.Application.Troubleshooting;

public class ArchiveTroubleshootingResultsHandler(IFileSystem fileSystem, ILocalFileSystem localFileSystem)
    : IRequestHandler<ArchiveTroubleshootingResults, Option<string>>
{
    public Task<Option<string>> Handle(ArchiveTroubleshootingResults request, CancellationToken cancellationToken)
    {
        string tempFile = Path.GetTempFileName();
        var hasReport = false;

        try
        {
            hasReport = AddResults(tempFile);
        }
        finally
        {
            // the archive must be closed before the file can be deleted
            if (!hasReport)
            {
                fileSystem.File.Delete(tempFile);
            }
        }

        return Task.FromResult(hasReport ? tempFile : Option<string>.None);
    }

    private bool AddResults(string tempFile)
    {
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
                string fileName = fileSystem.Path.GetFileName(file);

                bool isReport = fileName.StartsWith("ffmpeg-", StringComparison.OrdinalIgnoreCase)
                                 || fileName.StartsWith("ffreport", StringComparison.OrdinalIgnoreCase);

                hasReport = hasReport || isReport;

                bool shouldArchive = isReport
                    || fileName.Equals("logs.txt", StringComparison.OrdinalIgnoreCase)
                    || fileSystem.Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("capabilities", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("stream-selector", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("music-video-credits", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("outcome", StringComparison.OrdinalIgnoreCase);

                if (shouldArchive)
                {
                    string relativePath = fileSystem.Path.GetRelativePath(
                        FileSystemLayout.TranscodeTroubleshootingFolder,
                        file);

                    if (zipArchive.GetEntry(fileName) != null)
                    {
                        // next duplicates media_info.json, but we don't want to bury everything in a subfolder,
                        // so specifically handle this case and let any unexpected conflicts end up in a subfolder
                        if (relativePath.StartsWith(".troubleshooting_", StringComparison.OrdinalIgnoreCase))
                        {
                            fileName = $"next_{fileName}";
                        }
                        else
                        {
                            fileName = relativePath;
                        }
                    }

                    zipArchive.CreateEntryFromFile(file, fileName);
                }
            }
        }

        return hasReport;
    }
}
