using System.IO.Abstractions;
using ErsatzTV.Application.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Tests.Subtitles;

[TestFixture]
public class SubtitleNulBytesTests
{
    [Test]
    public async Task Strip_should_replace_subtitle_and_remove_temporary_file()
    {
        var fileSystem = new MockFileSystem();
        string directory = fileSystem.Path.GetTempPath();
        fileSystem.Directory.CreateDirectory(directory);
        string path = fileSystem.Path.Combine(directory, "subtitle.ass");
        await fileSystem.File.WriteAllBytesAsync(path, [65, 0, 66]);

        (await SubtitleNulBytes.Strip(fileSystem, path, CancellationToken.None)).ShouldBeTrue();

        (await fileSystem.File.ReadAllBytesAsync(path)).ShouldBe(new byte[] { 65, 66 });
        fileSystem.Directory.GetFiles(directory).ShouldBe(new[] { path });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Strip_should_preserve_original_when_write_fails(bool cancel)
    {
        var storage = new MockFileSystem();
        string directory = storage.Path.GetTempPath();
        storage.Directory.CreateDirectory(directory);
        string path = storage.Path.Combine(directory, "subtitle.ass");
        byte[] original = [65, 0, 66];
        await storage.File.WriteAllBytesAsync(path, original);

        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IFile file = Substitute.For<IFile>();
        fileSystem.File.Returns(file);
        file.ReadAllBytesAsync(path, Arg.Any<CancellationToken>()).Returns(original);
        file.WriteAllBytesAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storage.File.WriteAllBytes(call.ArgAt<string>(0), [65]);
                return Task.FromException(cancel ? new OperationCanceledException() : new IOException());
            });
        file.When(f => f.Delete(Arg.Any<string>()))
            .Do(call => storage.File.Delete(call.Arg<string>()));

        if (cancel)
        {
            await Should.ThrowAsync<OperationCanceledException>(() =>
                SubtitleNulBytes.Strip(fileSystem, path, CancellationToken.None));
        }
        else
        {
            await Should.ThrowAsync<IOException>(() =>
                SubtitleNulBytes.Strip(fileSystem, path, CancellationToken.None));
        }

        (await storage.File.ReadAllBytesAsync(path)).ShouldBe(original);
        storage.Directory.GetFiles(directory).ShouldBe(new[] { path });
        file.DidNotReceive().Move(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Test]
    public async Task StripOrDelete_should_report_stripped_and_unchanged()
    {
        var fileSystem = new MockFileSystem();
        string directory = fileSystem.Path.GetTempPath();
        fileSystem.Directory.CreateDirectory(directory);
        string path = fileSystem.Path.Combine(directory, "subtitle.ass");
        await fileSystem.File.WriteAllBytesAsync(path, [65, 0, 66]);

        (await SubtitleNulBytes.StripOrDelete(fileSystem, path, NullLogger.Instance, CancellationToken.None))
            .ShouldBe(SubtitleNulBytes.StripResult.Stripped);
        (await SubtitleNulBytes.StripOrDelete(fileSystem, path, NullLogger.Instance, CancellationToken.None))
            .ShouldBe(SubtitleNulBytes.StripResult.Unchanged);
        (await fileSystem.File.ReadAllBytesAsync(path)).ShouldBe(new byte[] { 65, 66 });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StripOrDelete_should_delete_subtitle_when_strip_fails(bool unauthorized)
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IFile file = Substitute.For<IFile>();
        fileSystem.File.Returns(file);
        file.ReadAllBytesAsync("subtitle.ass", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]>(
                unauthorized ? new UnauthorizedAccessException() : new IOException()));

        (await SubtitleNulBytes.StripOrDelete(fileSystem, "subtitle.ass", NullLogger.Instance, CancellationToken.None))
            .ShouldBe(SubtitleNulBytes.StripResult.Failed);

        file.Received(1).Delete("subtitle.ass");
    }

    [Test]
    public async Task StripOrDelete_should_report_failed_when_delete_fails()
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IFile file = Substitute.For<IFile>();
        fileSystem.File.Returns(file);
        file.ReadAllBytesAsync("subtitle.ass", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]>(new UnauthorizedAccessException()));
        file.When(f => f.Delete("subtitle.ass")).Do(_ => throw new UnauthorizedAccessException());

        (await SubtitleNulBytes.StripOrDelete(fileSystem, "subtitle.ass", NullLogger.Instance, CancellationToken.None))
            .ShouldBe(SubtitleNulBytes.StripResult.Failed);
    }
}
