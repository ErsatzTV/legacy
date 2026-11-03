using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ErsatzTV.Application.Playouts;

public enum NextPlayoutFolderPlanKind
{
    NoChange,
    InPlace,
    FullSwap
}

public record NextPlayoutFolderPlan(
    NextPlayoutFolderPlanKind Kind,
    List<string> FilesToWrite,
    List<string> FilesToDelete)
{
    public static readonly NextPlayoutFolderPlan NoChange = new(NextPlayoutFolderPlanKind.NoChange, [], []);
    public static readonly NextPlayoutFolderPlan FullSwap = new(NextPlayoutFolderPlanKind.FullSwap, [], []);
}

// LiveHash covers the version and items next can still select (null if none),
// so trimming finished items is not a change
public record NextPlayoutFileState(string FileName, DateTimeOffset Start, DateOnly Day, string LiveHash)
{
    public static Option<NextPlayoutFileState> FromJson(string fileName, string json, DateTimeOffset cutoff)
    {
        string[] split = Path.GetFileNameWithoutExtension(fileName).Split('_');
        if (split.Length != 2 ||
            !long.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out long startMs))
        {
            return None;
        }

        DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(startMs);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            var builder = new StringBuilder(root.GetProperty("version").GetString());
            var hasLiveItems = false;

            foreach (JsonElement item in root.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("finish").GetDateTimeOffset() > cutoff)
                {
                    hasLiveItems = true;
                    builder.Append('\0');
                    builder.Append(item.GetRawText());
                }
            }

            string liveHash = hasLiveItems
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
                : null;

            return new NextPlayoutFileState(
                fileName,
                start,
                DateOnly.FromDateTime(start.ToLocalTime().DateTime),
                liveHash);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                       or FormatException)
        {
            return None;
        }
    }
}

public static class NextPlayoutFolderPlanner
{
    // next's transcoded-until time can trail wall clock
    public static readonly TimeSpan FinishedMargin = TimeSpan.FromMinutes(5);

    // next only opens the file for the time it transcodes; later files are safe to replace
    public static readonly TimeSpan InPlaceBuffer = TimeSpan.FromHours(1);

    public static DateTimeOffset Cutoff(DateTimeOffset now) => now - FinishedMargin;

    public static NextPlayoutFolderPlan Plan(
        IReadOnlyCollection<NextPlayoutFileState> existing,
        IReadOnlyCollection<NextPlayoutFileState> desired,
        DateTimeOffset now)
    {
        if (existing.DistinctBy(f => f.Day).Count() != existing.Count ||
            desired.DistinctBy(f => f.Day).Count() != desired.Count)
        {
            return NextPlayoutFolderPlan.FullSwap;
        }

        var existingByDay = existing.ToDictionary(f => f.Day);
        var desiredByDay = desired.ToDictionary(f => f.Day);
        DateTimeOffset horizon = now + InPlaceBuffer;

        List<string> filesToWrite = [];
        List<string> filesToDelete = [];

        foreach (DateOnly day in existingByDay.Keys.Union(desiredByDay.Keys))
        {
            NextPlayoutFileState oldFile = existingByDay.GetValueOrDefault(day);
            NextPlayoutFileState newFile = desiredByDay.GetValueOrDefault(day);

            if (string.Equals(oldFile?.LiveHash, newFile?.LiveHash, StringComparison.Ordinal))
            {
                // only finished items; next never reads this file again
                if (oldFile is not null && newFile is null)
                {
                    filesToDelete.Add(oldFile.FileName);
                }

                continue;
            }

            if (oldFile?.Start <= horizon || newFile?.Start <= horizon)
            {
                return NextPlayoutFolderPlan.FullSwap;
            }

            if (newFile is not null)
            {
                filesToWrite.Add(newFile.FileName);
            }

            if (oldFile is not null && oldFile.FileName != newFile?.FileName)
            {
                filesToDelete.Add(oldFile.FileName);
            }
        }

        return filesToWrite.Count == 0 && filesToDelete.Count == 0
            ? NextPlayoutFolderPlan.NoChange
            : new NextPlayoutFolderPlan(NextPlayoutFolderPlanKind.InPlace, filesToWrite, filesToDelete);
    }
}
