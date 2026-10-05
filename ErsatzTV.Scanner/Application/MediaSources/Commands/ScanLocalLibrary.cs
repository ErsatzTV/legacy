using ErsatzTV.Core;

namespace ErsatzTV.Scanner.Application.MediaSources;

public record ScanLocalLibrary(string BaseUrl, int LibraryId, bool ForceScan, bool DeepScan)
    : IRequest<Either<BaseError, string>>;
