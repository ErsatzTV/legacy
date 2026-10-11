using ErsatzTV.Application.Streaming;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Streaming;

[TestFixture]
public class NextLoggerTests
{
    [TestCase("ERROR", LogLevel.Error)]
    [TestCase("WARN", LogLevel.Warning)]
    [TestCase("INFO", LogLevel.Information)]
    [TestCase("DEBUG", LogLevel.Debug)]
    [TestCase("TRACE", LogLevel.Trace)]
    public void Parse_Should_Map_Level(string level, LogLevel expected)
    {
        NextLogLine result = ParseSome(
            $$"""{"timestamp":"2026-10-11T03:04:27.117536Z","level":"{{level}}","message":"hello","target":"ersatztv_channel"}""");

        result.Level.ShouldBe(expected);
    }

    [Test]
    public void Parse_Should_Read_Channel_From_Span()
    {
        NextLogLine result = ParseSome(
            """{"timestamp":"2026-10-11T03:04:27.117536Z","level":"DEBUG","message":"wrapper stderr line","target":"ffmpeg","span":{"number":"7","name":"channel"}}""");

        result.Channel.ShouldBe("7");
        result.Message.ShouldBe("wrapper stderr line");
    }

    [TestCase("ffmpeg", true)]
    [TestCase("ersatztv_channel::channel_session", false)]
    public void Parse_Should_Flag_Ffmpeg_Target(string target, bool expected)
    {
        NextLogLine result = ParseSome(
            $$$"""{"timestamp":"2026-10-11T03:04:27.117536Z","level":"DEBUG","message":"hello","target":"{{{target}}}","span":{"number":"7","name":"channel"}}""");

        result.IsFfmpeg.ShouldBe(expected);
    }

    [Test]
    public void Parse_Should_Allow_Missing_Span()
    {
        NextLogLine result = ParseSome(
            """{"timestamp":"2026-10-11T03:04:27.117536Z","level":"INFO","message":"hello","target":"ersatztv_channel"}""");

        result.Channel.ShouldBeNull();
    }

    [Test]
    public void Parse_Should_Keep_Multi_Line_Message()
    {
        NextLogLine result = ParseSome(
            """{"timestamp":"2026-10-11T03:04:27.117536Z","level":"INFO","message":"multi-line\nmessage","target":"ersatztv_channel","span":{"number":"7","name":"channel"}}""");

        result.Message.ShouldBe("multi-line\nmessage");
    }

    [TestCase("thread 'main' panicked at src/main.rs:1:1:")]
    [TestCase("error: unexpected argument '--foo' found")]
    [TestCase("{not json")]
    [TestCase("""{"level":"INFO"}""")]
    public void Parse_Should_Reject_Non_Log_Lines(string line) => NextLogger.Parse(line).IsNone.ShouldBeTrue();

    private static NextLogLine ParseSome(string line) =>
        NextLogger.Parse(line).IfNone(() => throw new AssertionException($"failed to parse {line}"));
}
