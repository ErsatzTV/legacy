using System.Runtime.InteropServices;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Libraries;

public static class LocalLibraryPaths
{
    public static bool IsFullyQualified(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // rejects invisible leading characters (NBSP, ZWSP, BOM), quotes and URI schemes
        char first = path[0];
        if (first is not ('/' or '\\') && !char.IsAsciiLetter(first))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        // .NET treats any \\ prefix as fully qualified; require at least \\server\share
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            string[] segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return false;
            }
        }

        return true;
    }

    public static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();

    public static bool AreSubPaths(string path1, string path2)
    {
        if (string.IsNullOrWhiteSpace(path1) || string.IsNullOrWhiteSpace(path2))
        {
            return false;
        }

        string one = NormalizePath(path1) + Path.DirectorySeparatorChar;
        string two = NormalizePath(path2) + Path.DirectorySeparatorChar;

        return one == two || one.StartsWith(two, StringComparison.OrdinalIgnoreCase) ||
               two.StartsWith(one, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Conflicts(LocalPath path1, LocalPath path2)
    {
        bool isConflict = AreSubPaths(path1.Path, path2.Path);

        if (isConflict)
        {
            bool imagesAndOtherVideos = path1.MediaKind is LibraryMediaKind.Images &&
                                        path2.MediaKind is LibraryMediaKind.OtherVideos
                                        || path2.MediaKind is LibraryMediaKind.Images &&
                                        path1.MediaKind is LibraryMediaKind.OtherVideos;

            if (imagesAndOtherVideos)
            {
                isConflict = false;
            }
        }

        return isConflict;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    public record LocalPath(LibraryMediaKind MediaKind, string Path);
}
