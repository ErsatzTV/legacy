using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.Subtitles;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using static ErsatzTV.Application.Channels.Mapper;
using Channel = ErsatzTV.Core.Domain.Channel;

namespace ErsatzTV.Application.Channels;

public class UpdateChannelHandler(
    ChannelWriter<IBackgroundServiceRequest> workerChannel,
    IDbContextFactory<TvContext> dbContextFactory,
    ISearchTargets searchTargets)
    : IRequestHandler<UpdateChannel, Either<BaseError, ChannelViewModel>>
{
    public async Task<Either<BaseError, ChannelViewModel>> Handle(
        UpdateChannel request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, Channel> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(c => ApplyUpdateRequest(dbContext, c, request, cancellationToken));
    }

    private async Task<ChannelViewModel> ApplyUpdateRequest(
        TvContext dbContext,
        Channel c,
        UpdateChannel update,
        CancellationToken cancellationToken)
    {
        // don't save mirror when playout exists
        if (c.Playouts.Count > 0)
        {
            update = update with
            {
                PlayoutSource = ChannelPlayoutSource.Generated,
                MirrorSourceChannelId = null
            };
        }

        bool hasEpgChange = c.PlayoutSource != update.PlayoutSource || c.ShowInEpg != update.ShowInEpg;
        bool hasPlayoutChange = hasEpgChange || c.WatermarkId != update.WatermarkId ||
                                c.PreferredAudioLanguageCode != update.PreferredAudioLanguageCode ||
                                c.PreferredAudioTitle != update.PreferredAudioTitle ||
                                c.PreferredSubtitleLanguageCode != update.PreferredSubtitleLanguageCode ||
                                c.MusicVideoCreditsMode != update.MusicVideoCreditsMode ||
                                c.SubtitleMode != update.SubtitleMode;

        c.Name = update.Name;
        c.Number = update.Number;
        c.SortNumber = double.Parse(update.Number, CultureInfo.InvariantCulture);
        c.Group = update.Group;
        c.Categories = update.Categories;
        c.FFmpegProfileId = update.FFmpegProfileId;
        c.SlugSeconds = update.SlugSeconds;
        c.StreamSelectorMode = update.StreamSelectorMode;
        c.StreamSelector = update.StreamSelector;
        c.PreferredAudioLanguageCode = update.PreferredAudioLanguageCode;
        c.PreferredAudioTitle = update.PreferredAudioTitle;
        c.PreferredSubtitleLanguageCode = update.PreferredSubtitleLanguageCode;
        c.SubtitleMode = update.SubtitleMode;
        c.MusicVideoCreditsMode = update.MusicVideoCreditsMode;
        c.MusicVideoCreditsTemplate = update.MusicVideoCreditsTemplate;
        c.SongVideoMode = update.SongVideoMode;
        c.TranscodeMode = update.TranscodeMode;
        c.IdleBehavior = update.IdleBehavior;
        c.IsEnabled = update.IsEnabled;
        c.ShowInEpg = update.IsEnabled && update.ShowInEpg;
        c.Artwork ??= [];

        if (!string.IsNullOrWhiteSpace(update.Logo?.Path))
        {
            string logo = update.Logo.Path;
            if (logo.StartsWith("iptv/logos/", StringComparison.Ordinal))
            {
                logo = logo.Replace("iptv/logos/", string.Empty);
            }

            Option<Artwork> maybeLogo = c.Artwork.Where(a => a.ArtworkKind == ArtworkKind.Logo).HeadOrNone();
            foreach (Artwork artwork in maybeLogo)
            {
                artwork.Path = logo;
                artwork.OriginalContentType = !string.IsNullOrEmpty(update.Logo.ContentType)
                    ? update.Logo.ContentType
                    : null;
                artwork.DateUpdated = DateTime.UtcNow;
            }

            if (maybeLogo.IsNone)
            {
                var artwork = new Artwork
                {
                    Path = logo,
                    OriginalContentType = !string.IsNullOrEmpty(update.Logo.ContentType)
                        ? update.Logo.ContentType
                        : null,
                    DateAdded = DateTime.UtcNow,
                    DateUpdated = DateTime.UtcNow,
                    ArtworkKind = ArtworkKind.Logo
                };
                c.Artwork.Add(artwork);
            }
        }
        else
        {
            await dbContext.Entry(c)
                .Collection(channel => channel.Artwork)
                .LoadAsync(cancellationToken);

            foreach (Artwork artwork in c.Artwork.Where(x => x.ArtworkKind is ArtworkKind.Logo).ToList())
            {
                c.Artwork.Remove(artwork);
                dbContext.Artwork.Remove(artwork);
            }
        }

        c.PlayoutSource = update.PlayoutSource;
        c.PlayoutMode = update.PlayoutMode;

        if (c.PlayoutSource is ChannelPlayoutSource.Mirror)
        {
            c.PlayoutMode = ChannelPlayoutMode.Continuous;
            hasEpgChange |= c.MirrorSourceChannelId != update.MirrorSourceChannelId;
            hasEpgChange |= c.PlayoutOffset != update.PlayoutOffset;
        }
        else
        {
            c.MirrorSourceChannelId = null;
            c.PlayoutOffset = null;
        }

        c.MirrorSourceChannelId = update.MirrorSourceChannelId;
        c.PlayoutOffset = update.PlayoutOffset;
        c.StreamingEngine = update.StreamingEngine;
        c.NextEngineTextSubtitleMode = update.NextEngineTextSubtitleMode;
        c.StreamingMode = update.StreamingMode;
        c.WatermarkId = update.WatermarkId;
        c.FallbackFillerId = update.FallbackFillerId;

        if (c.StreamingEngine is StreamingEngine.Next &&
            c.StreamingMode is not StreamingMode.HttpLiveStreamingSegmenter &&
            c.StreamingMode is not StreamingMode.TransportStreamHybrid)
        {
            c.StreamingMode = StreamingMode.HttpLiveStreamingSegmenter;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        searchTargets.SearchTargetsChanged();

        if (c.SubtitleMode != ChannelSubtitleMode.None)
        {
            Option<Playout> maybePlayout = await dbContext.Playouts
                .SelectOneAsync(p => p.ChannelId, p => p.ChannelId == c.Id, cancellationToken);

            foreach (Playout playout in maybePlayout)
            {
                await workerChannel.WriteAsync(new ExtractEmbeddedSubtitles(playout.Id), cancellationToken);
            }
        }

        await workerChannel.WriteAsync(new RefreshChannelList(), cancellationToken);
        if (hasEpgChange)
        {
            await workerChannel.WriteAsync(new RefreshChannelData(c.Number), cancellationToken);
        }
        if (hasPlayoutChange)
        {
            await workerChannel.WriteAsync(new SyncNextPlayout(c.Number), cancellationToken);
        }

        return ProjectToViewModel(c, c.Playouts?.Count ?? 0);
    }

    private static async Task<Validation<BaseError, Channel>> Validate(
        TvContext dbContext,
        UpdateChannel request,
        CancellationToken cancellationToken) =>
        (await ChannelMustExist(dbContext, request, cancellationToken),
            ValidateName(request),
            await ValidateNumber(dbContext, request, cancellationToken),
            await MirrorSourceMustBeValid(dbContext, request, cancellationToken))
        .Apply((channelToUpdate, _, _, _) => channelToUpdate);

    private static Task<Validation<BaseError, Channel>> ChannelMustExist(
        TvContext dbContext,
        UpdateChannel updateChannel,
        CancellationToken cancellationToken) =>
        dbContext.Channels
            .Include(c => c.Artwork)
            .Include(c => c.Watermark)
            .Include(c => c.Playouts)
            .SelectOneAsync(c => c.Id, c => c.Id == updateChannel.ChannelId, cancellationToken)
            .Map(o => o.ToValidation<BaseError>("Channel does not exist."));

    private static async Task<Validation<BaseError, Unit>> MirrorSourceMustBeValid(
        TvContext dbContext,
        UpdateChannel request,
        CancellationToken cancellationToken)
    {
        if (request.PlayoutSource is not ChannelPlayoutSource.Mirror)
        {
            return Unit.Default;
        }

        Option<Channel> maybeMirrorSource = await dbContext.Channels
            .AsNoTracking()
            .SelectOneAsync(
                c => c.Id == request.MirrorSourceChannelId,
                c => c.Id == request.MirrorSourceChannelId,
                cancellationToken);

        if (maybeMirrorSource.IsNone)
        {
            return BaseError.New("Mirror source channel does not exist.");
        }

        foreach (var mirrorSource in maybeMirrorSource)
        {
            if (mirrorSource.PlayoutSource is not ChannelPlayoutSource.Generated)
            {
                return BaseError.New(
                    $"Mirror source channel {mirrorSource.Name} must use generated playout source");
            }
        }

        foreach (TimeSpan playoutOffset in Optional(request.PlayoutOffset))
        {
            if (playoutOffset < TimeSpan.FromHours(-12) || playoutOffset > TimeSpan.FromHours(12))
            {
                return BaseError.New("Playout offset must not be greater than 12 hours");
            }
        }

        return Unit.Default;
    }

    private static Validation<BaseError, string> ValidateName(UpdateChannel updateChannel) =>
        updateChannel.NotEmpty(c => c.Name)
            .Bind(_ => updateChannel.NotLongerThan(50)(c => c.Name));

    private static async Task<Validation<BaseError, string>> ValidateNumber(
        TvContext dbContext,
        UpdateChannel updateChannel,
        CancellationToken cancellationToken)
    {
        int matchId = await dbContext.Channels
            .SelectOneAsync(c => c.Number, c => c.Number == updateChannel.Number, cancellationToken)
            .Match(c => c.Id, () => updateChannel.ChannelId);

        if (matchId == updateChannel.ChannelId)
        {
            if (Regex.IsMatch(updateChannel.Number, Channel.NumberValidator))
            {
                return updateChannel.Number;
            }

            return BaseError.New("Invalid channel number; two decimals are allowed for subchannels");
        }

        return BaseError.New("Channel number must be unique");
    }
}
