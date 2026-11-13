using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Scheduling;

public record PlayoutParameters(
    DateTimeOffset Start,
    DateTimeOffset Finish,
    Map<CollectionKey, List<MediaItem>> CollectionMediaItems,
    CollectionItemsCache CollectionItemsCache);
