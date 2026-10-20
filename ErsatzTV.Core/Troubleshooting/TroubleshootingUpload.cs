using System.Text.RegularExpressions;
using ErsatzTV.Core.FFmpeg;

namespace ErsatzTV.Core.Troubleshooting;

public static partial class TroubleshootingUpload
{
    public const string Url = "https://upload.ersatztv.org/v1/";

    // server rejects unknown key sets; rename when recipients change
    public const string KeySet = "2026a";

    public const long MaxCiphertextLength = 25_000_000;

    // primary, then offline backup
    public static readonly string[] Recipients =
    [
        "age1kg0zg8aqa7tysckwagn4tjwaj6nf77qs93p26l922g8jl7pl74vqwvvgwc",
        "age1vv5nzvzvkwd0u2pjpn4uq80ydaw7v8hjnrp8jhekdmzegwljrv0sz2lnf2"
    ];

    private static readonly string ArchivePrefix = $"{TempFileCategory.TroubleshootingUpload}_".ToLowerInvariant();

    public static bool IsValidId(string id) => id is not null && IdRegex().IsMatch(id);

    public static string FormatId(string id) => $"etv-{id[..4]}-{id[4..]}";

    // ETV_CONFIG_FOLDER is not normalized
    public static bool IsArchivePath(string path) =>
        Path.GetDirectoryName(path) is { Length: > 0 } directory &&
        Path.GetFullPath(directory) == Path.GetFullPath(FileSystemLayout.TempFilePoolFolder) &&
        Path.GetFileName(path).StartsWith(ArchivePrefix, StringComparison.Ordinal);

    // plaintext; don't keep across restarts
    public static void DeleteArchives()
    {
        if (!Directory.Exists(FileSystemLayout.TempFilePoolFolder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(FileSystemLayout.TempFilePoolFolder, $"{ArchivePrefix}*"))
        {
            File.Delete(file);
        }
    }

    [GeneratedRegex("^[0-9a-hjkmnp-tv-z]{10}$")]
    private static partial Regex IdRegex();
}
