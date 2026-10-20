using System.IO.Abstractions;
using System.IO.Compression;
using ErsatzTV.Application.Troubleshooting;
using ErsatzTV.Core;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Core.Troubleshooting;
using ErsatzTV.Infrastructure.Troubleshooting;
using LanguageExt;
using MediatR;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using static LanguageExt.Prelude;

namespace ErsatzTV.Tests.Troubleshooting;

[TestFixture]
public class TroubleshootingUploadHandlerTests
{
    private static readonly string ArchivePath =
        Path.Combine(FileSystemLayout.TempFilePoolFolder, "troubleshootingupload_0");

    [Test]
    public async Task Prepare_should_refuse_while_playback_runs()
    {
        IEntityLocker? locker = Substitute.For<IEntityLocker>();
        locker.IsTroubleshootingPlaybackLocked().Returns(true);
        IMediator? mediator = Substitute.For<IMediator>();

        Either<BaseError, PreparedTroubleshootingUpload> result =
            await Prepare(locker, mediator, new MockFileSystem(), 100);

        result.IsLeft.ShouldBeTrue();
        await mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<ArchiveTroubleshootingResults>());
    }

    [Test]
    public async Task Prepare_should_fail_without_report()
    {
        IMediator? mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ArchiveTroubleshootingResults>(), Arg.Any<CancellationToken>())
            .Returns(Option<string>.None);

        Either<BaseError, PreparedTroubleshootingUpload> result =
            await Prepare(Substitute.For<IEntityLocker>(), mediator, new MockFileSystem(), 100);

        result.IsLeft.ShouldBeTrue();
    }

    [TestCase(TroubleshootingUpload.MaxCiphertextLength, false)]
    [TestCase(TroubleshootingUpload.MaxCiphertextLength + 1, true)]
    public async Task Prepare_should_list_the_archive_that_was_built(long ciphertextLength, bool tooLarge)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(FileSystemLayout.TempFilePoolFolder);
        byte[] zip = Zip(("logs.txt", 5), ("ffmpeg-1.log", 3));
        fileSystem.File.WriteAllBytes(ArchivePath, zip);

        IMediator? mediator = Substitute.For<IMediator>();
        mediator.Send(
                Arg.Is<ArchiveTroubleshootingResults>(r => r.OutputPath == ArchivePath),
                Arg.Any<CancellationToken>())
            .Returns(Some(ArchivePath));

        Either<BaseError, PreparedTroubleshootingUpload> result =
            await Prepare(Substitute.For<IEntityLocker>(), mediator, fileSystem, ciphertextLength);

        PreparedTroubleshootingUpload prepared = result.RightToSeq().Single();
        prepared.ArchivePath.ShouldBe(ArchivePath);
        prepared.Entries.ShouldBe(
        [
            new PreparedTroubleshootingUploadEntry("ffmpeg-1.log", 3),
            new PreparedTroubleshootingUploadEntry("logs.txt", 5)
        ]);
        prepared.ArchiveSize.ShouldBe(zip.Length);
        prepared.TooLarge.ShouldBe(tooLarge);
    }

    [Test]
    public async Task Upload_should_encrypt_send_and_delete()
    {
        MockFileSystem fileSystem = FileSystemWithArchive(1000);
        ITroubleshootingUploadClient? client = Substitute.For<ITroubleshootingUploadClient>();
        long sent = 0;
        client.Upload(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Stream? stream = call.Arg<Stream>();
                sent = stream.Length - stream.Position;
                return Right<TroubleshootingUploadError, string>("w4th3azcgk");
            });

        Either<TroubleshootingUploadError, string> result = await Upload(fileSystem, client, ArchivePath);

        result.RightToSeq().ToList().ShouldBe(["w4th3azcgk"]);
        sent.ShouldBe(new AgeEncryptor().GetCiphertextLength(1000, TroubleshootingUpload.Recipients.Length));
        fileSystem.File.Exists(ArchivePath).ShouldBeFalse();
    }

    [Test]
    public async Task Upload_should_keep_archive_after_failure()
    {
        MockFileSystem fileSystem = FileSystemWithArchive(1000);
        ITroubleshootingUploadClient? client = Substitute.For<ITroubleshootingUploadClient>();
        client.Upload(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Left<TroubleshootingUploadError, string>(new TroubleshootingUploadError(503, "disabled")));

        Either<TroubleshootingUploadError, string> result = await Upload(fileSystem, client, ArchivePath);

        result.LeftToSeq().Single().StatusCode.ShouldBe(503);
        fileSystem.File.Exists(ArchivePath).ShouldBeTrue();
    }

    [Test]
    public async Task Upload_should_refuse_paths_outside_the_pool()
    {
        string outside = Path.Combine(FileSystemLayout.AppDataFolder, "troubleshootingupload_0");
        MockFileSystem fileSystem = FileSystemWithArchive(10);
        fileSystem.File.WriteAllBytes(outside, [1, 2, 3]);
        ITroubleshootingUploadClient? client = Substitute.For<ITroubleshootingUploadClient>();

        Either<TroubleshootingUploadError, string> result = await Upload(fileSystem, client, outside);

        result.IsLeft.ShouldBeTrue();
        await client.DidNotReceiveWithAnyArgs().Upload(null!, null!, CancellationToken.None);
    }

    [Test]
    public async Task Upload_should_refuse_missing_archive()
    {
        ITroubleshootingUploadClient? client = Substitute.For<ITroubleshootingUploadClient>();

        Either<TroubleshootingUploadError, string> result = await Upload(new MockFileSystem(), client, ArchivePath);

        result.IsLeft.ShouldBeTrue();
        await client.DidNotReceiveWithAnyArgs().Upload(null!, null!, CancellationToken.None);
    }

    [Test]
    public async Task Upload_should_refuse_archive_over_the_cap()
    {
        MockFileSystem fileSystem = FileSystemWithArchive(10);
        IAgeEncryptor? encryptor = Substitute.For<IAgeEncryptor>();
        encryptor.GetCiphertextLength(Arg.Any<long>(), Arg.Any<int>())
            .Returns(TroubleshootingUpload.MaxCiphertextLength + 1);
        ITroubleshootingUploadClient? client = Substitute.For<ITroubleshootingUploadClient>();

        var handler = new UploadTroubleshootingArchiveHandler(encryptor, client, fileSystem);
        Either<TroubleshootingUploadError, string> result =
            await handler.Handle(new UploadTroubleshootingArchive(ArchivePath), CancellationToken.None);

        result.IsLeft.ShouldBeTrue();
        encryptor.DidNotReceiveWithAnyArgs().Encrypt(null!, null!, null!);
    }

    [Test]
    public async Task Discard_should_only_delete_pool_archives()
    {
        string outside = Path.Combine(FileSystemLayout.AppDataFolder, "troubleshootingupload_0");
        MockFileSystem fileSystem = FileSystemWithArchive(10);
        fileSystem.File.WriteAllBytes(outside, [1]);
        var handler = new DiscardTroubleshootingUploadHandler(fileSystem);

        await handler.Handle(new DiscardTroubleshootingUpload(outside), CancellationToken.None);
        await handler.Handle(new DiscardTroubleshootingUpload(ArchivePath), CancellationToken.None);

        fileSystem.File.Exists(outside).ShouldBeTrue();
        fileSystem.File.Exists(ArchivePath).ShouldBeFalse();
    }

    [Test]
    public async Task Archive_should_replace_a_stale_file_at_the_output_path()
    {
        string dir = Directory.CreateTempSubdirectory("troubleshooting-archive-tests").FullName;
        try
        {
            string report = Path.Combine(dir, "ffmpeg-1.log");
            await File.WriteAllTextAsync(report, "report");
            string output = Path.Combine(dir, "out.zip");
            await File.WriteAllBytesAsync(output, Zip(("stale.txt", 4)));

            ILocalFileSystem? localFileSystem = Substitute.For<ILocalFileSystem>();
            localFileSystem.ListSubdirectories(Arg.Any<string>()).Returns([]);
            localFileSystem.ListFiles(FileSystemLayout.TranscodeTroubleshootingFolder).Returns([report]);

            var handler = new ArchiveTroubleshootingResultsHandler(new RealFileSystem(), localFileSystem);
            Option<string> result = await handler.Handle(
                new ArchiveTroubleshootingResults(output),
                CancellationToken.None);

            result.ShouldBe(Some(output));
            using ZipArchive zip = ZipFile.OpenRead(output);
            zip.Entries.Select(e => e.FullName).ShouldBe(["ffmpeg-1.log"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Archive_path_should_reject_paths_without_a_directory()
    {
        TroubleshootingUpload.IsArchivePath(ArchivePath).ShouldBeTrue();
        TroubleshootingUpload.IsArchivePath("troubleshootingupload_0").ShouldBeFalse();
        TroubleshootingUpload.IsArchivePath(null!).ShouldBeFalse();
    }

    private static async Task<Either<BaseError, PreparedTroubleshootingUpload>> Prepare(
        IEntityLocker locker,
        IMediator mediator,
        IFileSystem fileSystem,
        long ciphertextLength)
    {
        ITempFilePool? tempFilePool = Substitute.For<ITempFilePool>();
        tempFilePool.GetNextTempFile(TempFileCategory.TroubleshootingUpload).Returns(ArchivePath);
        IAgeEncryptor? encryptor = Substitute.For<IAgeEncryptor>();
        encryptor.GetCiphertextLength(Arg.Any<long>(), Arg.Any<int>()).Returns(ciphertextLength);

        var handler = new PrepareTroubleshootingUploadHandler(mediator, locker, tempFilePool, encryptor, fileSystem);
        return await handler.Handle(new PrepareTroubleshootingUpload(), CancellationToken.None);
    }

    private static async Task<Either<TroubleshootingUploadError, string>> Upload(
        IFileSystem fileSystem,
        ITroubleshootingUploadClient client,
        string path)
    {
        var handler = new UploadTroubleshootingArchiveHandler(new AgeEncryptor(), client, fileSystem);
        return await handler.Handle(new UploadTroubleshootingArchive(path), CancellationToken.None);
    }

    private static MockFileSystem FileSystemWithArchive(int size)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(FileSystemLayout.TempFilePoolFolder);
        fileSystem.Directory.CreateDirectory(FileSystemLayout.AppDataFolder);
        fileSystem.File.WriteAllBytes(ArchivePath, new byte[size]);
        return fileSystem;
    }

    private static byte[] Zip(params (string Name, int Size)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach ((string name, int size) in entries)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(new byte[size]);
            }
        }

        return stream.ToArray();
    }
}
