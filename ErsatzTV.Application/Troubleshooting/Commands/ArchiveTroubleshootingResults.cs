namespace ErsatzTV.Application.Troubleshooting;

public record ArchiveTroubleshootingResults(string OutputPath = null) : IRequest<Option<string>>;
