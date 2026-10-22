using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Subtitles;

public static class SubtitleNulBytes
{
    // some files end the ASS header with a NUL and `-c:s copy` keeps it;
    // ffmpeg stops reading at the NUL, so all events are lost
    public static async Task<bool> Strip(IFileSystem fileSystem, string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await fileSystem.File.ReadAllBytesAsync(path, cancellationToken);
        if (Array.IndexOf(bytes, (byte)0) < 0)
        {
            return false;
        }

        byte[] stripped = bytes.Where(b => b != 0).ToArray();
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await fileSystem.File.WriteAllBytesAsync(temporaryPath, stripped, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.File.Move(temporaryPath, path, true);
        }
        finally
        {
            fileSystem.File.Delete(temporaryPath);
        }

        return true;
    }

    // a missing file is extracted again, so delete is a safe fallback
    public static async Task<StripResult> StripOrDelete(
        IFileSystem fileSystem,
        string path,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Strip(fileSystem, path, cancellationToken) ? StripResult.Stripped : StripResult.Unchanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to strip NUL bytes from subtitle {Path}; deleting it", path);
        }

        try
        {
            fileSystem.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete subtitle {Path}", path);
        }

        return StripResult.Failed;
    }

    public enum StripResult
    {
        Unchanged,
        Stripped,
        Failed
    }
}
