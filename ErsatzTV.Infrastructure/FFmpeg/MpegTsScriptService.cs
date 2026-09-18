using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Builders;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Infrastructure.FFmpeg;

public class MpegTsScriptService(
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem,
    ITempFilePool tempFilePool,
    ILogger<MpegTsScriptService> logger) : IMpegTsScriptService
{
    private static readonly SemaphoreSlim Slim = new(1, 1);
    private static readonly ConcurrentDictionary<string, MpegTsScript> Scripts = new();

    public async Task RefreshScripts(CancellationToken cancellationToken)
    {
        await Slim.WaitAsync(cancellationToken);
        try
        {
            var folderList = localFileSystem.ListSubdirectories(FileSystemLayout.MpegTsScriptsFolder).ToList();

            foreach (string folder in folderList)
            {
                string definition = fileSystem.Path.Combine(folder, "mpegts.yml");
                if (fileSystem.File.Exists(definition))
                {
                    Option<MpegTsScript> maybeScript = FromYaml(
                        await fileSystem.File.ReadAllTextAsync(definition, cancellationToken));
                    foreach (var script in maybeScript)
                    {
                        script.Id = fileSystem.Path.GetFileName(folder);
                        Scripts[folder] = script;
                    }
                }
            }

            foreach (string missingScript in Scripts.Keys.Except(folderList))
            {
                Scripts.TryRemove(missingScript, out _);
            }
        }
        finally
        {
            Slim.Release();
        }
    }

    public List<MpegTsScript> GetScripts() => Scripts.Values.ToList();

    public async Task<Option<Command>> Execute(MpegTsScript script, Channel channel, string hlsUrl, string ffmpegPath)
    {
        string scriptFolder = fileSystem.Path.Combine(FileSystemLayout.MpegTsScriptsFolder, script.Id);

        // the values below are passed through the environment (which the OS delivers as utf-16)
        // rather than templated into the script; cmd.exe decodes batch files using the console
        // code page, and at 65001 it aborts entirely on any multi-byte character
        string channelName = channel.Name.Replace("\"", string.Empty);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (string.IsNullOrWhiteSpace(script.WindowsScript))
            {
                logger.LogWarning("mpeg-ts script {Id} has no windows script", script.Id);
                return Option<Command>.None;
            }

            string scriptInput = fileSystem.Path.Combine(scriptFolder, script.WindowsScript);
            if (fileSystem.File.Exists(scriptInput))
            {
                Option<string> maybeScript = await GetTemplatedScript(
                    scriptInput,
                    hlsUrl,
                    channel.Name,
                    ffmpegPath);
                foreach (string finalScript in maybeScript)
                {
                    var fileName = $"{tempFilePool.GetNextTempFile(TempFileCategory.MpegTsScript)}.bat";
                    await fileSystem.File.WriteAllTextAsync(fileName, finalScript);
                    return Cli.Wrap(fileName)
                        .WithEnvironmentVariables(WithScriptVariables(hlsUrl, channelName, ffmpegPath));
                }
            }
            else
            {
                logger.LogWarning(
                    "mpeg-ts script {Id}'s windows script does not exist at {File}",
                    script.Id,
                    scriptInput);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(script.LinuxScript))
            {
                logger.LogWarning("mpeg-ts script {Id} has no linux script", script.Id);
                return Option<Command>.None;
            }

            string scriptInput = fileSystem.Path.Combine(scriptFolder, script.LinuxScript);
            if (fileSystem.File.Exists(scriptInput))
            {
                Option<string> maybeScript = await GetTemplatedScript(
                    scriptInput,
                    hlsUrl,
                    channel.Name,
                    ffmpegPath);
                foreach (string finalScript in maybeScript)
                {
                    string fileName = tempFilePool.GetNextTempFile(TempFileCategory.MpegTsScript);
                    await fileSystem.File.WriteAllTextAsync(fileName, finalScript);
                    return Cli.Wrap("bash")
                        .WithArguments([fileName])
                        .WithEnvironmentVariables(WithScriptVariables(hlsUrl, channelName, ffmpegPath));
                }
            }
            else
            {
                logger.LogWarning(
                    "mpeg-ts script {Id}'s linux script does not exist at {File}",
                    script.Id,
                    scriptInput);

            }
        }

        return Option<Command>.None;
    }

    private static Action<EnvironmentVariablesBuilder> WithScriptVariables(
        string hlsUrl,
        string channelName,
        string ffmpegPath) =>
        builder => builder
            .Set("ETV_HLS_URL", hlsUrl)
            .Set("ETV_CHANNEL_NAME", channelName)
            .Set("ETV_FFMPEG_PATH", ffmpegPath);

    private async Task<Option<string>> GetTemplatedScript(
        string fileName,
        string hlsUrl,
        string channelName,
        string ffmpegPath)
    {
        string script = await fileSystem.File.ReadAllTextAsync(fileName);
        try
        {
            var data = new Dictionary<string, string>
            {
                ["HlsUrl"] = hlsUrl,
                ["ChannelName"] = channelName,
                ["FFmpegPath"] = ffmpegPath
            };

            var scriptObject = new ScriptObject();
            scriptObject.Import(data, renamer: member => member.Name);
            var context = new TemplateContext { MemberRenamer = member => member.Name };
            context.PushGlobal(scriptObject);
            return await Template.Parse(script).RenderAsync(context);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to render mpegts script as scriban template");
            return Option<string>.None;
        }
    }

    private Option<MpegTsScript> FromYaml(string yaml)
    {
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            return deserializer.Deserialize<MpegTsScript>(yaml);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load mpegts script YAML definition");
            return Option<MpegTsScript>.None;
        }
    }
}
