using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.FFmpeg;

public interface IGraphicsElementSelector
{
    List<PlayoutItemGraphicsElement> SelectGraphicsElements(
        Channel channel,
        PlayoutItem playoutItem,
        DateTimeOffset now,
        bool shouldLogMessages);
}
