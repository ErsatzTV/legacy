using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Troubleshooting;

namespace ErsatzTV.Core.Troubleshooting;

public class TroubleshootingPlayoutItemStore : ITroubleshootingPlayoutItemStore
{
    private volatile TroubleshootingPlayoutItem _current;

    public void Store(Channel channel, PlayoutItem playoutItem) => _current = new TroubleshootingPlayoutItem(channel, playoutItem);

    public Option<TroubleshootingPlayoutItem> Current() => Optional(_current);

    public void Clear() => _current = null;
}
