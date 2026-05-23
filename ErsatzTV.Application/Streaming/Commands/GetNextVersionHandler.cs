using System.IO.Abstractions;
using System.Text;
using CliWrap;
using ErsatzTV.Core;

namespace ErsatzTV.Application.Streaming;

public class GetNextVersionHandler(IFileSystem fileSystem)
    : NextChannelHandlerBase(fileSystem), IRequestHandler<GetNextVersion, string>
{
    public async Task<string> Handle(GetNextVersion request, CancellationToken cancellationToken)
    {
        try
        {
            Validation<BaseError, string> validation = await ChannelBinaryMustExist();
            foreach (string channelBinary in validation.SuccessToSeq())
            {
                var stdOutBuffer = new StringBuilder();
                CommandResult command = await Cli.Wrap(channelBinary)
                    .WithArguments(["--version"])
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cancellationToken);

                if (command.IsSuccess)
                {
                    return stdOutBuffer.ToString().Replace("ersatztv-channel", string.Empty).Trim();
                }
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return "n/a";
    }
}
