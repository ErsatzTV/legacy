using ErsatzTV.Core.Next.Description;

namespace ErsatzTV.Application.Streaming;

public record GetNextDescribe : IRequest<Option<Describe>>;
