using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;

namespace ErsatzTV.Application.Configuration;

public class GetMpegTsScriptsHandler(IMpegTsScriptService mpegTsScriptService)
    : IRequestHandler<GetMpegTsScripts, List<MpegTsScript>>
{
    public async Task<List<MpegTsScript>> Handle(GetMpegTsScripts request, CancellationToken cancellationToken)
    {
        await mpegTsScriptService.RefreshScripts(cancellationToken);
        return mpegTsScriptService.GetScripts().OrderBy(x => x.Name).ToList();
    }
}
