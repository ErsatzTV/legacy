using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Refit;

namespace ErsatzTV.Infrastructure;

public static class RefitJson
{
    // Refit 17 no longer falls back to reflection-based JSON; this restores the Refit 16 behavior
    // (Web defaults include AllowReadingFromString, which Refit 17 dropped from its own defaults)
    public static readonly RefitSettings Settings = new()
    {
        ContentSerializer = new SystemTextJsonContentSerializer(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            })
    };
}
