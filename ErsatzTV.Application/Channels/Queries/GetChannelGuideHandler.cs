using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Iptv;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IO;

namespace ErsatzTV.Application.Channels;

public partial class GetChannelGuideHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    RecyclableMemoryStreamManager recyclableMemoryStreamManager,
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem)
    : IRequestHandler<GetChannelGuide, Either<BaseError, ChannelGuide>>
{
    public async Task<Either<BaseError, ChannelGuide>> Handle(
        GetChannelGuide request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var hiddenChannelNumbers = dbContext.Channels
            .Where(c => c.ShowInEpg == false)
            .Select(c => c.Number)
            .AsEnumerable()
            .Select(n => $"{n}.xml")
            .ToImmutableHashSet();

        string channelsFile = fileSystem.Path.Combine(FileSystemLayout.ChannelGuideCacheFolder, "channels.xml");
        if (!fileSystem.File.Exists(channelsFile))
        {
            return BaseError.New($"Required file {channelsFile} is missing");
        }

        long mtime = fileSystem.File.GetLastWriteTime(channelsFile).Ticks;

        // cached guide fragments still use the {AccessTokenUri} placeholder, which now only carries the cache buster
        var cacheBusterUri = $"?v={mtime}";

        string channelsFragment = await ReadAllTextShared(channelsFile, cancellationToken);

        // TODO: is regex faster?
        channelsFragment = channelsFragment
            .Replace("{RequestBase}", $"{request.Scheme}://{request.Host}{request.BaseUrl}")
            .Replace("{AccessTokenUri}", cacheBusterUri);

        var channelDataFragments = new Dictionary<string, string>();

        foreach (string fileName in localFileSystem.ListFiles(FileSystemLayout.ChannelGuideCacheFolder))
        {
            if (fileName.Contains("channels"))
            {
                continue;
            }

            if (hiddenChannelNumbers.Contains(fileSystem.Path.GetFileName(fileName)))
            {
                continue;
            }

            try
            {
                string channelDataFragment = await ReadAllTextShared(fileName, cancellationToken);

                channelDataFragment = channelDataFragment
                    .Replace("{RequestBase}", $"{request.Scheme}://{request.Host}{request.BaseUrl}")
                    .Replace("{AccessTokenUri}", cacheBusterUri);

                channelDataFragment = EtvTagRegex().Replace(channelDataFragment, string.Empty);

                channelDataFragments.Add(fileSystem.Path.GetFileNameWithoutExtension(fileName), channelDataFragment);
            }
            catch (FileNotFoundException)
            {
                // ignore this channel fragment
            }
            catch (IOException)
            {
                // ignore this channel fragment
            }
        }

        return new ChannelGuide(recyclableMemoryStreamManager, channelsFragment, channelDataFragments);
    }

    private async Task<string> ReadAllTextShared(string fileName, CancellationToken cancellationToken)
    {
        await using var stream = fileSystem.FileStream.New(
            fileName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    [GeneratedRegex(@"<etv:[^>]+?>.*?<\/etv:[^>]+?>|<etv:[^>]+?\/>", RegexOptions.Singleline)]
    private static partial Regex EtvTagRegex();
}
