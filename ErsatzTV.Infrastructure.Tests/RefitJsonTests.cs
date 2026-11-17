using System.Net;
using System.Text;
using ErsatzTV.Infrastructure.Jellyfin;
using ErsatzTV.Infrastructure.Jellyfin.Models;
using NUnit.Framework;
using Refit;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests;

[TestFixture]
public class RefitJsonTests
{
    [Test]
    public async Task Settings_Should_Deserialize_Generated_Client_Responses()
    {
        const string Json =
            """{"items":[{"name":"Movie","productionYear":"2001","runTimeTicks":123}],"totalRecordCount":"1"}""";

        var client = new HttpClient(new StubHandler(Json)) { BaseAddress = new Uri("http://localhost") };
        IJellyfinApi api = RestService.ForGenerated<IJellyfinApi>(client, RefitJson.Settings);

        JellyfinLibraryItemsResponse response = await api.GetMovieLibraryItems("auth", "parent");

        response.TotalRecordCount.ShouldBe(1);
        response.Items.Count.ShouldBe(1);
        response.Items[0].Name.ShouldBe("Movie");
        response.Items[0].ProductionYear.ShouldBe(2001);
        response.Items[0].RunTimeTicks.ShouldBe(123);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
    }
}
