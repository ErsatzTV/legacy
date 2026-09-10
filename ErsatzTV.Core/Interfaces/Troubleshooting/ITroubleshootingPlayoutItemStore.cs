using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Troubleshooting;

public record TroubleshootingPlayoutItem(Channel Channel, PlayoutItem PlayoutItem);

/// <summary>
/// Media item troubleshooting builds a synthetic playout item that never reaches the database;
/// this holds it so the graphics canvas endpoint can serve the troubleshooting channel.
/// </summary>
public interface ITroubleshootingPlayoutItemStore
{
    void Store(Channel channel, PlayoutItem playoutItem);
    Option<TroubleshootingPlayoutItem> Current();
    void Clear();
}
