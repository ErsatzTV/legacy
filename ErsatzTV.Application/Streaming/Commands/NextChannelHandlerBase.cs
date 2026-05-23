using System.IO.Abstractions;
using System.Runtime.InteropServices;
using ErsatzTV.Core;

namespace ErsatzTV.Application.Streaming;

public abstract class NextChannelHandlerBase(IFileSystem fileSystem)
{
    protected Task<Validation<BaseError, string>> ChannelBinaryMustExist()
    {
        string nextFolder = SystemEnvironment.NextFolder;
        if (string.IsNullOrWhiteSpace(nextFolder))
        {
            string processFileName = Environment.ProcessPath ?? string.Empty;
            string processExecutable = Path.GetFileNameWithoutExtension(processFileName);
            nextFolder = Path.GetDirectoryName(processFileName);
            if ("dotnet".Equals(processExecutable, StringComparison.OrdinalIgnoreCase))
            {
                nextFolder = AppContext.BaseDirectory;
            }
        }

        string executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ersatztv-channel.exe"
            : "ersatztv-channel";

        string channelBinary = fileSystem.Path.Combine(ReplaceTilde(nextFolder), executable);
        if (!fileSystem.Path.Exists(channelBinary))
        {
            return Task.FromResult<Validation<BaseError, string>>(
                BaseError.New("ersatztv-channel binary does not exist!"));
        }

        return Task.FromResult<Validation<BaseError, string>>(channelBinary);
    }

    private string ReplaceTilde(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        string userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        switch (path)
        {
            case "~":
                return userFolder;
            case not null
                when path.Length == 2 &&
                     (path[1] == fileSystem.Path.DirectorySeparatorChar ||
                      path[1] == fileSystem.Path.AltDirectorySeparatorChar):
                return userFolder + fileSystem.Path.DirectorySeparatorChar;
            default:
                return fileSystem.Path.Combine(userFolder, path[2..]);
        }
    }
}
