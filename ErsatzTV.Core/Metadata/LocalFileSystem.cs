using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Security.Cryptography;
using ErsatzTV.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Metadata;

public class LocalFileSystem(IFileSystem fileSystem, ILogger<LocalFileSystem> logger) : ILocalFileSystem
{
    public Unit EnsureFolderExists(string folder)
    {
        try
        {
            if (folder != null && !fileSystem.Directory.Exists(folder))
            {
                fileSystem.Directory.CreateDirectory(folder);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to ensure folder exists at {Folder}", folder);
        }

        return Unit.Default;
    }

    public DateTime GetLastWriteTime(string path)
    {
        try
        {
            return fileSystem.File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return SystemTime.MinValueUtc;
        }
    }

    public IEnumerable<string> ListSubdirectories(string folder)
    {
        if (fileSystem.Directory.Exists(folder))
        {
            try
            {
                return fileSystem.Directory.EnumerateDirectories(folder);
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogWarning("Unauthorized access exception listing subdirectories of folder {Folder}", folder);
            }
            catch (Exception)
            {
                // do nothing
            }
        }

        return new List<string>();
    }

    public IEnumerable<string> ListFiles(string folder)
    {
        if (fileSystem.Directory.Exists(folder))
        {
            try
            {
                return fileSystem.Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase));
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogWarning("Unauthorized access exception listing files in folder {Folder}", folder);
            }
            catch (Exception)
            {
                // do nothing
            }
        }

        return new List<string>();
    }

    public IEnumerable<string> ListFiles(string folder, string searchPattern)
    {
        if (folder is not null && fileSystem.Directory.Exists(folder))
        {
            try
            {
                return fileSystem.Directory.EnumerateFiles(folder, searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase));
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogWarning("Unauthorized access exception listing files in folder {Folder}", folder);
            }
            catch (Exception)
            {
                // do nothing
            }
        }

        return new List<string>();
    }

    public IEnumerable<string> ListFiles(string folder, params string[] searchPatterns)
    {
        if (folder is not null && fileSystem.Directory.Exists(folder))
        {
            try
            {
                return searchPatterns
                    .SelectMany(searchPattern =>
                        fileSystem.Directory.EnumerateFiles(folder, searchPattern, SearchOption.TopDirectoryOnly)
                            .Where(path =>
                                !Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase)))
                    .Distinct();
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogWarning("Unauthorized access exception listing files in folder {Folder}", folder);
            }
            catch (Exception)
            {
                // do nothing
            }
        }

        return new List<string>();
    }

    public async Task<Either<BaseError, Unit>> CopyFile(string source, string destination)
    {
        try
        {
            string directory = Path.GetDirectoryName(destination) ?? string.Empty;
            if (!fileSystem.Directory.Exists(directory))
            {
                fileSystem.Directory.CreateDirectory(directory);
            }

            await using FileSystemStream sourceStream = fileSystem.File.OpenRead(source);
            await using FileSystemStream destinationStream = fileSystem.File.Create(destination);
            await sourceStream.CopyToAsync(destinationStream);

            return Unit.Default;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    public Unit EmptyFolder(string folder)
    {
        try
        {
            foreach (string file in fileSystem.Directory.GetFiles(folder))
            {
                fileSystem.File.Delete(file);
            }

            foreach (string directory in fileSystem.Directory.GetDirectories(folder))
            {
                fileSystem.Directory.Delete(directory, true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to empty folder at {Folder}", folder);
        }

        return Unit.Default;
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms")]
    public async Task<byte[]> GetHash(string path)
    {
        // ReSharper disable once UseAwaitUsing
        // ReSharper disable once ConvertToUsingDeclaration
        using (FileSystemStream fs = fileSystem.File.OpenRead(path))
        {
            fs.Position = 0;
            return await MD5.HashDataAsync(fs);
        }
    }

    public string GetCustomOrDefaultFile(string folder, string file)
    {
        string path = Path.Combine(folder, file);
        return fileSystem.File.Exists(path) ? path : Path.Combine(folder, $"_{file}");
    }
}
