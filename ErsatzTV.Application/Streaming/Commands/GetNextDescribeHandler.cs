using System.IO.Abstractions;
using System.Text;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Next.Description;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetNextDescribeHandler(
    IFileSystem fileSystem,
    IMemoryCache memoryCache,
    ILogger<GetNextDescribeHandler> logger)
    : NextChannelHandlerBase(fileSystem), IRequestHandler<GetNextDescribe, Option<Describe>>
{
    private const string CacheKey = "next.describe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<Option<Describe>> Handle(GetNextDescribe request, CancellationToken cancellationToken)
    {
        // cache only successes so a failed describe retries on the next session start
        if (memoryCache.TryGetValue(CacheKey, out Describe cached))
        {
            return cached;
        }

        try
        {
            Validation<BaseError, string> validation = await ChannelBinaryMustExist();
            foreach (string channelBinary in validation.SuccessToSeq())
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(Timeout);

                var stdOutBuffer = new StringBuilder();
                CommandResult command = await Cli.Wrap(channelBinary)
                    .WithArguments(["describe"])
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cts.Token);

                // older binaries lack describe and exit with a usage error
                if (command.IsSuccess)
                {
                    Describe describe = Describe.FromJson(stdOutBuffer.ToString());
                    if (describe is not null)
                    {
                        memoryCache.Set(CacheKey, describe);
                        return describe;
                    }
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Failed to describe ersatztv-channel");
        }

        return Option<Describe>.None;
    }
}
