using System.Net;
using System.Text;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Core.Troubleshooting;
using ErsatzTV.Infrastructure.Troubleshooting;
using LanguageExt;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Troubleshooting;

[TestFixture]
public class TroubleshootingUploadClientTests
{
    private static readonly byte[] Ciphertext = "age-encryption.org/v1\n-> X25519 test"u8.ToArray();

    [Test]
    public async Task Should_send_one_tus_creation_with_upload_post()
    {
        var handler = new FakeHandler(_ => Created("https://upload.ersatztv.org/v1/w4th3azcgk", Ciphertext.Length));

        Either<TroubleshootingUploadError, string> result = await Upload(handler, "26.11.3-abc");

        result.RightToSeq().ToList().ShouldBe(["w4th3azcgk"]);
        handler.Requests.Count.ShouldBe(1);

        RecordedRequest request = handler.Requests[0];
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri(TroubleshootingUpload.Url));
        request.Headers["Tus-Resumable"].ShouldBe("1.0.0");
        request.Headers["Upload-Length"].ShouldBe(Ciphertext.Length.ToString());
        request.Headers["Content-Type"].ShouldBe("application/offset+octet-stream");
        request.Body.ShouldBe(Ciphertext);

        var metadata = request.Headers["Upload-Metadata"]
            .Split(',')
            .Select(pair => pair.Split(' '))
            .ToDictionary(p => p[0], p => Encoding.UTF8.GetString(Convert.FromBase64String(p[1])));
        metadata.ShouldBe(
            new Dictionary<string, string> { ["version"] = "26.11.3-abc", ["keys"] = TroubleshootingUpload.KeySet });
    }

    [Test]
    public async Task Should_return_status_and_body_on_rejection()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
        {
            Content = new StringContent("Upgrade ErsatzTV to upload troubleshooting results.\n")
        });

        Either<TroubleshootingUploadError, string> result = await Upload(handler);

        result.LeftToSeq().ToList().ShouldBe(
            [new TroubleshootingUploadError(426, "Upgrade ErsatzTV to upload troubleshooting results.")]);
    }

    [TestCase(null)]
    [TestCase("https://upload.ersatztv.org/v1/")]
    [TestCase("https://upload.ersatztv.org/v1/W4TH3AZCGK")]
    [TestCase("https://upload.ersatztv.org/v1/w4th3azcgu")]
    [TestCase("https://upload.ersatztv.org/v1/../etc")]
    public async Task Should_reject_invalid_id(string? location)
    {
        var handler = new FakeHandler(_ => Created(location, Ciphertext.Length));

        Either<TroubleshootingUploadError, string> result = await Upload(handler);

        result.IsLeft.ShouldBeTrue();
    }

    [Test]
    public async Task Should_reject_short_offset()
    {
        var handler = new FakeHandler(_ => Created("https://upload.ersatztv.org/v1/w4th3azcgk", Ciphertext.Length - 1));

        Either<TroubleshootingUploadError, string> result = await Upload(handler);

        result.IsLeft.ShouldBeTrue();
    }

    [Test]
    public async Task Should_return_error_when_unreachable()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("Name or service not known"));

        Either<TroubleshootingUploadError, string> result = await Upload(handler);

        result.LeftToSeq().Single().StatusCode.ShouldBeNull();
    }

    [Test]
    public async Task Should_return_error_on_timeout()
    {
        var handler = new FakeHandler(_ => throw new TaskCanceledException());

        Either<TroubleshootingUploadError, string> result = await Upload(handler);

        result.LeftToSeq().Single().Message.ShouldBe("The upload timed out.");
    }

    [Test]
    public async Task Should_throw_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Should.ThrowAsync<TaskCanceledException>(() => Upload(handler, cancellationToken: cts.Token));
    }

    [Test]
    public void Should_ship_valid_recipients()
    {
        TroubleshootingUpload.Recipients.Length.ShouldBe(2);
        foreach (string recipient in TroubleshootingUpload.Recipients)
        {
            Should.NotThrow(() => AgeEncryptor.ParseRecipient(recipient));
        }
    }

    [TestCase("w4th3azcgk", ExpectedResult = "etv-w4th-3azcgk")]
    public string Should_format_id(string id) => TroubleshootingUpload.FormatId(id);

    private static async Task<Either<TroubleshootingUploadError, string>> Upload(
        FakeHandler handler,
        string version = "test",
        CancellationToken cancellationToken = default)
    {
        IHttpClientFactory? factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(TroubleshootingUploadClient.HttpClientName).Returns(_ => new HttpClient(handler, false));

        var client = new TroubleshootingUploadClient(factory);
        return await client.Upload(new MemoryStream(Ciphertext), version, cancellationToken);
    }

    private static HttpResponseMessage Created(string? location, long offset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created);
        if (location is not null)
        {
            response.Headers.Location = new Uri(location);
        }

        response.Headers.Add("Upload-Offset", offset.ToString());
        return response;
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, Dictionary<string, string> Headers, byte[] Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers
                .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value));

            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, headers, body));

            return respond(request);
        }
    }
}
