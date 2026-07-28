using System.IO.Abstractions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Images;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErsatzTV.Core.FFmpeg;

public class WatermarkSelector(
    IFileSystem fileSystem,
    IImageCache imageCache,
    IDecoSelector decoSelector,
    ILogger<WatermarkSelector> logger)
    : IWatermarkSelector
{
    public List<WatermarkOptions> SelectWatermarks(
        Option<ChannelWatermark> globalWatermark,
        Channel channel,
        PlayoutItem playoutItem,
        DateTimeOffset now,
        bool shouldLogMessages)
    {
        ILogger<WatermarkSelector> log = shouldLogMessages ? logger : NullLogger<WatermarkSelector>.Instance;

        log.LogDebug("Checking for watermark at {Now}", now);

        var result = new List<WatermarkOptions>();

        if (channel.StreamingMode == StreamingMode.HttpLiveStreamingDirect)
        {
            return result;
        }

        if (playoutItem.DisableWatermarks)
        {
            log.LogDebug("Watermark is disabled by playout item");
            return result;
        }

        DecoEntries decoEntries = decoSelector.GetDecoEntries(playoutItem.Playout, now);

        // first, check deco template / active deco
        foreach (Deco templateDeco in decoEntries.TemplateDeco)
        {
            var done = false;

            switch (templateDeco.WatermarkMode)
            {
                case DecoMode.Merge:
                    if (playoutItem.FillerKind is FillerKind.None || templateDeco.UseWatermarkDuringFiller)
                    {
                        log.LogDebug("Watermark will come from template deco (merge)");
                        result.AddRange(
                            OptionsForWatermarks(channel, templateDeco.DecoWatermarks.Map(dwm => dwm.Watermark), log));
                        break;
                    }

                    log.LogDebug("Watermark is disabled by template deco during filler");
                    result.Clear();
                    done = true;
                    break;
                case DecoMode.Override:
                    if (playoutItem.FillerKind is FillerKind.None || templateDeco.UseWatermarkDuringFiller)
                    {
                        log.LogDebug("Watermark will come from template deco (replace)");
                        result.AddRange(
                            OptionsForWatermarks(channel, templateDeco.DecoWatermarks.Map(dwm => dwm.Watermark), log));
                        done = true;
                        break;
                    }

                    log.LogDebug("Watermark is disabled by template deco during filler");
                    result.Clear();
                    done = true;
                    break;
                case DecoMode.Disable:
                    log.LogDebug("Watermark is disabled by template deco");
                    done = true;
                    break;
                case DecoMode.Inherit:
                    log.LogDebug("Watermark will inherit from playout deco");
                    break;
            }

            if (done)
            {
                return result;
            }
        }

        // second, check playout deco
        foreach (Deco playoutDeco in decoEntries.PlayoutDeco)
        {
            var done = false;

            switch (playoutDeco.WatermarkMode)
            {
                case DecoMode.Merge:
                    if (playoutItem.FillerKind is FillerKind.None || playoutDeco.UseWatermarkDuringFiller)
                    {
                        log.LogDebug("Watermark will come from playout deco (merge)");
                        result.AddRange(
                            OptionsForWatermarks(channel, playoutDeco.DecoWatermarks.Map(dwm => dwm.Watermark), log));
                        break;
                    }

                    log.LogDebug("Watermark is disabled by playout deco during filler");
                    result.Clear();
                    done = true;
                    break;
                case DecoMode.Override:
                    if (playoutItem.FillerKind is FillerKind.None || playoutDeco.UseWatermarkDuringFiller)
                    {
                        log.LogDebug("Watermark will come from playout deco (replace)");
                        result.AddRange(
                            OptionsForWatermarks(channel, playoutDeco.DecoWatermarks.Map(dwm => dwm.Watermark), log));
                        done = true;
                        break;
                    }

                    log.LogDebug("Watermark is disabled by playout deco during filler");
                    result.Clear();
                    done = true;
                    break;
                case DecoMode.Disable:
                    log.LogDebug("Watermark is disabled by playout deco");
                    done = true;
                    break;
                case DecoMode.Inherit:
                    log.LogDebug("Watermark will inherit from channel and/or global setting");
                    break;
            }

            if (done)
            {
                return result;
            }
        }

        if (playoutItem.Watermarks.Count > 0)
        {
            foreach (var watermark in playoutItem.Watermarks)
            {
                Option<WatermarkOptions> options = GetWatermarkOptions(
                    channel,
                    watermark,
                    Option<ChannelWatermark>.None,
                    shouldLogMessages);
                result.AddRange(options);
            }

            return result;
        }


        Option<WatermarkOptions> finalOptions = GetWatermarkOptions(
            channel,
            Option<ChannelWatermark>.None,
            globalWatermark,
            shouldLogMessages);
        result.AddRange(finalOptions);

        return result;
    }

    public Option<WatermarkOptions> GetWatermarkOptions(
        Channel channel,
        Option<ChannelWatermark> playoutItemWatermark,
        Option<ChannelWatermark> globalWatermark,
        bool shouldLogMessages)
    {
        ILogger<WatermarkSelector> log = shouldLogMessages ? logger : NullLogger<WatermarkSelector>.Instance;

        if (channel.StreamingMode == StreamingMode.HttpLiveStreamingDirect)
        {
            return Option<WatermarkOptions>.None;
        }

        // check for playout item watermark
        foreach (ChannelWatermark watermark in playoutItemWatermark)
        {
            switch (watermark.ImageSource)
            {
                // used for song progress overlay
                case ChannelWatermarkImageSource.Resource:
                    string resourcePath = fileSystem.Path.Combine(
                        FileSystemLayout.ResourcesCacheFolder,
                        watermark.Image);
                    if (fileSystem.File.Exists(resourcePath))
                    {
                        return new WatermarkOptions(watermark, resourcePath, Option<int>.None);
                    }

                    log.LogWarning(
                        "Watermark resource no longer exists at {Path} and will be ignored",
                        resourcePath);
                    return None;
                case ChannelWatermarkImageSource.Custom:
                    // bad form validation makes this possible
                    if (string.IsNullOrWhiteSpace(watermark.Image))
                    {
                        log.LogWarning(
                            "Watermark {Name} has custom image configured with no image; ignoring",
                            watermark.Name);
                        break;
                    }

                    log.LogDebug("Watermark will come from playout item (custom)");

                    string customPath = imageCache.GetPathForImage(
                        watermark.Image,
                        ArtworkKind.Watermark,
                        Option<int>.None);

                    if (fileSystem.File.Exists(customPath))
                    {
                        return new WatermarkOptions(watermark, customPath, None);
                    }

                    log.LogWarning(
                        "Custom watermark no longer exists at {Path} and will be ignored",
                        customPath);
                    return None;
                case ChannelWatermarkImageSource.ChannelLogo:
                    log.LogDebug("Watermark will come from playout item (channel logo)");

                    string channelPath = ChannelLogoGenerator.GenerateChannelLogoUrl(channel);
                    Option<Artwork> maybeLogoArtwork =
                        Optional(channel.Artwork.Find(a => a.ArtworkKind == ArtworkKind.Logo));
                    foreach (var logoArtwork in maybeLogoArtwork)
                    {
                        channelPath = Artwork.IsExternalUrl(logoArtwork.Path)
                            ? logoArtwork.Path
                            : imageCache.GetPathForImage(logoArtwork.Path, ArtworkKind.Logo, Option<int>.None);
                    }

                    // urls (external logos, generated channel logos) can't be checked on the file system
                    if (Artwork.IsExternalUrl(channelPath) || fileSystem.File.Exists(channelPath))
                    {
                        return new WatermarkOptions(watermark, channelPath, None);
                    }

                    log.LogWarning(
                        "Channel logo no longer exists at {Path} and will be ignored",
                        channelPath);
                    return None;
                default:
                    throw new NotSupportedException("Unsupported watermark image source");
            }
        }

        // check for channel watermark
        if (channel.Watermark != null)
        {
            switch (channel.Watermark.ImageSource)
            {
                case ChannelWatermarkImageSource.Custom:
                    log.LogDebug("Watermark will come from channel (custom)");

                    string customPath = imageCache.GetPathForImage(
                        channel.Watermark.Image,
                        ArtworkKind.Watermark,
                        Option<int>.None);

                    if (fileSystem.File.Exists(customPath))
                    {
                        return new WatermarkOptions(channel.Watermark, customPath, None);
                    }

                    log.LogWarning(
                        "Custom watermark no longer exists at {Path} and will be ignored",
                        customPath);
                    return None;
                case ChannelWatermarkImageSource.ChannelLogo:
                    log.LogDebug("Watermark will come from channel (channel logo)");

                    string channelPath = ChannelLogoGenerator.GenerateChannelLogoUrl(channel);
                    Option<Artwork> maybeLogoArtwork =
                        Optional(channel.Artwork.Find(a => a.ArtworkKind == ArtworkKind.Logo));
                    foreach (var logoArtwork in maybeLogoArtwork)
                    {
                        channelPath = Artwork.IsExternalUrl(logoArtwork.Path)
                            ? logoArtwork.Path
                            : imageCache.GetPathForImage(logoArtwork.Path, ArtworkKind.Logo, Option<int>.None);
                    }

                    // urls (external logos, generated channel logos) can't be checked on the file system
                    if (Artwork.IsExternalUrl(channelPath) || fileSystem.File.Exists(channelPath))
                    {
                        return new WatermarkOptions(channel.Watermark, channelPath, None);
                    }

                    log.LogWarning(
                        "Channel logo no longer exists at {Path} and will be ignored",
                        channelPath);
                    return None;
                default:
                    throw new NotSupportedException("Unsupported watermark image source");
            }
        }

        // check for global watermark
        foreach (ChannelWatermark watermark in globalWatermark)
        {
            switch (watermark.ImageSource)
            {
                case ChannelWatermarkImageSource.Custom:
                    log.LogDebug("Watermark will come from global (custom)");

                    string customPath = imageCache.GetPathForImage(
                        watermark.Image,
                        ArtworkKind.Watermark,
                        Option<int>.None);

                    if (fileSystem.File.Exists(customPath))
                    {
                        return new WatermarkOptions(watermark, customPath, None);
                    }

                    log.LogWarning(
                        "Custom watermark no longer exists at {Path} and will be ignored",
                        customPath);
                    return None;
                case ChannelWatermarkImageSource.ChannelLogo:
                    log.LogDebug("Watermark will come from global (channel logo)");

                    string channelPath = ChannelLogoGenerator.GenerateChannelLogoUrl(channel);
                    Option<Artwork> maybeLogoArtwork =
                        Optional(channel.Artwork.Find(a => a.ArtworkKind == ArtworkKind.Logo));
                    foreach (var logoArtwork in maybeLogoArtwork)
                    {
                        channelPath = Artwork.IsExternalUrl(logoArtwork.Path)
                            ? logoArtwork.Path
                            : imageCache.GetPathForImage(logoArtwork.Path, ArtworkKind.Logo, Option<int>.None);
                    }

                    // urls (external logos, generated channel logos) can't be checked on the file system
                    if (Artwork.IsExternalUrl(channelPath) || fileSystem.File.Exists(channelPath))
                    {
                        return new WatermarkOptions(watermark, channelPath, None);
                    }

                    log.LogWarning(
                        "Channel logo no longer exists at {Path} and will be ignored",
                        channelPath);
                    return None;
                default:
                    throw new NotSupportedException("Unsupported watermark image source");
            }
        }

        return Option<WatermarkOptions>.None;
    }

    private List<WatermarkOptions> OptionsForWatermarks(
        Channel channel,
        IEnumerable<ChannelWatermark> watermarks,
        ILogger<WatermarkSelector> log)
    {
        var result = new List<WatermarkOptions>();

        foreach (var watermark in watermarks)
        {
            result.AddRange(GetWatermarkOptions(channel, watermark, log));
        }

        return result;
    }

    private Option<WatermarkOptions> GetWatermarkOptions(
        Channel channel,
        ChannelWatermark watermark,
        ILogger<WatermarkSelector> log)
    {
        switch (watermark.ImageSource)
        {
            // used for song progress overlay
            case ChannelWatermarkImageSource.Resource:
                return new WatermarkOptions(
                    watermark,
                    Path.Combine(FileSystemLayout.ResourcesCacheFolder, watermark.Image),
                    Option<int>.None);
            case ChannelWatermarkImageSource.Custom:
                // bad form validation makes this possible
                if (string.IsNullOrWhiteSpace(watermark.Image))
                {
                    log.LogWarning(
                        "Watermark {Name} has custom image configured with no image; ignoring",
                        watermark.Name);
                    break;
                }

                string customPath = imageCache.GetPathForImage(
                    watermark.Image,
                    ArtworkKind.Watermark,
                    Option<int>.None);
                return new WatermarkOptions(
                    watermark,
                    customPath,
                    None);
            case ChannelWatermarkImageSource.ChannelLogo:
                string channelPath = ChannelLogoGenerator.GenerateChannelLogoUrl(channel);
                Option<Artwork> maybeLogoArtwork =
                    Optional(channel.Artwork.Find(a => a.ArtworkKind == ArtworkKind.Logo));
                foreach (var logoArtwork in maybeLogoArtwork)
                {
                    channelPath = Artwork.IsExternalUrl(logoArtwork.Path)
                        ? logoArtwork.Path
                        : imageCache.GetPathForImage(logoArtwork.Path, ArtworkKind.Logo, Option<int>.None);
                }

                return new WatermarkOptions(watermark, channelPath, None);
            default:
                throw new NotSupportedException("Unsupported watermark image source");
        }

        return Option<WatermarkOptions>.None;
    }
}
