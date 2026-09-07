using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErsatzTV.Core.Interfaces.Metadata;

namespace ErsatzTV.Scanner.Core.Metadata;

public static class FolderEtag
{
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms")]
    public static string Calculate(string folder, ILocalFileSystem localFileSystem)
    {
        IEnumerable<string> allFiles = localFileSystem.ListFiles(folder);

        var sb = new StringBuilder();
        foreach (string file in allFiles.OrderBy(identity))
        {
            sb.Append(file);
            sb.Append(localFileSystem.GetLastWriteTime(file).Ticks);
        }

        var hash = new StringBuilder();
        byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        foreach (byte t in bytes)
        {
            hash.Append(t.ToString("x2", CultureInfo.InvariantCulture));
        }

        return hash.ToString();
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms")]
    public static string CalculateWithSubfolders(string folder, ILocalFileSystem localFileSystem)
    {
        IEnumerable<string> allFiles = localFileSystem.ListFiles(folder);

        var sb = new StringBuilder();
        foreach (string file in allFiles.OrderBy(identity))
        {
            sb.Append(file);
            sb.Append(localFileSystem.GetLastWriteTime(file).Ticks);
        }

        foreach (string subfolder in localFileSystem.ListSubdirectories(folder).OrderBy(identity))
        {
            sb.Append(subfolder);
            sb.Append(Calculate(subfolder, localFileSystem));
        }

        var hash = new StringBuilder();
        byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        foreach (byte t in bytes)
        {
            hash.Append(t.ToString("x2", CultureInfo.InvariantCulture));
        }

        return hash.ToString();
    }
}
