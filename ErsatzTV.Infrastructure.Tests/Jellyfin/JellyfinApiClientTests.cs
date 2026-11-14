using ErsatzTV.Infrastructure.Jellyfin;
using ErsatzTV.Infrastructure.Jellyfin.Models;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Jellyfin;

[TestFixture]
public class JellyfinApiClientTests
{
    private static JellyfinMediaStreamResponse Stream(int index, bool external = false) =>
        new() { Index = index, IsExternal = external };

    [Test]
    public void SourceStreamIndex_Should_Skip_External_Streams_Listed_First()
    {
        List<JellyfinMediaStreamResponse> streams =
            [Stream(0, true), Stream(1, true), Stream(2), Stream(3), Stream(4)];

        JellyfinApiClient.SourceStreamIndex(streams, 2).ShouldBe(0);
        JellyfinApiClient.SourceStreamIndex(streams, 3).ShouldBe(1);
        JellyfinApiClient.SourceStreamIndex(streams, 4).ShouldBe(2);
    }

    [Test]
    public void SourceStreamIndex_Should_Keep_Indexes_When_External_Streams_Listed_Last()
    {
        List<JellyfinMediaStreamResponse> streams = [Stream(0), Stream(1), Stream(2, true)];

        JellyfinApiClient.SourceStreamIndex(streams, 0).ShouldBe(0);
        JellyfinApiClient.SourceStreamIndex(streams, 1).ShouldBe(1);
    }

    [Test]
    public void SourceStreamIndex_Should_Keep_Indexes_Without_External_Streams()
    {
        List<JellyfinMediaStreamResponse> streams = [Stream(0), Stream(1), Stream(2)];

        JellyfinApiClient.SourceStreamIndex(streams, 2).ShouldBe(2);
    }
}
