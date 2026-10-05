using ErsatzTV.Core;

namespace ErsatzTV.Application.MediaSources;

public interface IScanLocalLibrary : IRequest<Either<BaseError, string>>, IScannerBackgroundServiceRequest
{
    int LibraryId { get; }
    bool ForceScan { get; }
    bool DeepScan { get; }
}

public record ScanLocalLibraryIfNeeded(int LibraryId) : IScanLocalLibrary
{
    public bool ForceScan => false;
    public bool DeepScan => false;
}

public record ForceScanLocalLibrary(int LibraryId, bool DeepScan = false) : IScanLocalLibrary
{
    public bool ForceScan => true;
}
