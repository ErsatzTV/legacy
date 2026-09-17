using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.FFmpegProfiles;

public class CreateFFmpegProfileHandler :
    IRequestHandler<CreateFFmpegProfile, Either<BaseError, CreateFFmpegProfileResult>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ISearchTargets _searchTargets;

    public CreateFFmpegProfileHandler(IDbContextFactory<TvContext> dbContextFactory, ISearchTargets searchTargets)
    {
        _dbContextFactory = dbContextFactory;
        _searchTargets = searchTargets;
    }

    public async Task<Either<BaseError, CreateFFmpegProfileResult>> Handle(
        CreateFFmpegProfile request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, FFmpegProfile> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(profile => PersistFFmpegProfile(dbContext, profile));
    }

    private async Task<CreateFFmpegProfileResult> PersistFFmpegProfile(
        TvContext dbContext,
        FFmpegProfile ffmpegProfile)
    {
        await dbContext.FFmpegProfiles.AddAsync(ffmpegProfile);
        await dbContext.SaveChangesAsync();
        _searchTargets.SearchTargetsChanged();
        return new CreateFFmpegProfileResult(ffmpegProfile.Id);
    }

    private static async Task<Validation<BaseError, FFmpegProfile>> Validate(
        TvContext dbContext,
        CreateFFmpegProfile request,
        CancellationToken cancellationToken) =>
        (await ValidateName(dbContext, request), ValidateThreadCount(request),
            await ResolutionMustExist(dbContext, request, cancellationToken))
        .Apply((name, threadCount, resolutionId) =>
        {
            var hwAccel = request.NormalizeVideo
                ? request.HardwareAcceleration
                : HardwareAccelerationKind.None;

            return new FFmpegProfile
            {
                Name = name,
                ThreadCount = threadCount,

                NormalizeAudio = request.NormalizeAudio,
                NormalizeVideo = request.NormalizeVideo,

                HardwareAcceleration = hwAccel,
                VaapiDriver = request.VaapiDriver,
                VaapiDevice = request.VaapiDevice,
                QsvExtraHardwareFrames = request.QsvExtraHardwareFrames,
                ResolutionId = resolutionId,
                ScalingBehavior = request.ScalingBehavior,

                // only allow customization with VAAPI accel
                PadMode = hwAccel switch
                {
                    HardwareAccelerationKind.None => FilterMode.Software,
                    HardwareAccelerationKind.Vaapi => request.PadMode,
                    _ => FilterMode.HardwareIfPossible
                },

                VideoFormat = request.NormalizeVideo ? request.VideoFormat : FFmpegProfileVideoFormat.Copy,
                VideoProfile = request.VideoProfile,
                VideoPreset = request.VideoPreset,
                AllowBFrames = request.AllowBFrames,

                // mpeg2video only supports 8-bit content
                BitDepth = request.VideoFormat is FFmpegProfileVideoFormat.Mpeg2Video
                    ? FFmpegProfileBitDepth.EightBit
                    : request.BitDepth,

                VideoBitrate = request.VideoBitrate,
                VideoBufferSize = request.VideoBufferSize,
                TonemapAlgorithm = request.TonemapAlgorithm,
                AudioFormat = request.NormalizeAudio ? request.AudioFormat : FFmpegProfileAudioFormat.Copy,
                AudioBitrate = request.AudioBitrate,
                AudioBufferSize = request.AudioBufferSize,

                NormalizeLoudnessMode = request.NormalizeLoudnessMode,
                TargetLoudness = request.NormalizeLoudnessMode is NormalizeLoudnessMode.LoudNorm
                    ? request.TargetLoudness
                    : null,

                AudioChannels = request.AudioChannels,
                AudioSampleRate = request.AudioSampleRate,
                NormalizeFramerate = request.NormalizeFramerate,
                NormalizeColors = request.NormalizeColors,
                DeinterlaceVideo = request.DeinterlaceVideo
            };
        });

    private static async Task<Validation<BaseError, string>> ValidateName(
        TvContext dbContext, CreateFFmpegProfile createFFmpegProfile)
    {
        if (string.IsNullOrWhiteSpace(createFFmpegProfile.Name) || createFFmpegProfile.Name.Length > 50)
        {
            return BaseError.BadRequest($"FFmpeg profile name \"{createFFmpegProfile.Name}\" is invalid");
        }

        Option<FFmpegProfile> maybeExisting = await dbContext.FFmpegProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(ff => ff.Name == createFFmpegProfile.Name)
            .Map(Optional);

        return maybeExisting.IsSome
            ? BaseError.Conflict(
                $"An ffmpeg profile named \"{createFFmpegProfile.Name}\" already exists in the database")
            : Success<BaseError, string>(createFFmpegProfile.Name);
    }

    private static Validation<BaseError, int> ValidateThreadCount(CreateFFmpegProfile createFFmpegProfile) =>
        createFFmpegProfile.AtLeast(0)(p => p.ThreadCount);

    private static Task<Validation<BaseError, int>> ResolutionMustExist(
        TvContext dbContext,
        CreateFFmpegProfile createFFmpegProfile,
        CancellationToken cancellationToken) =>
        dbContext.Resolutions
            .SelectOneAsync(r => r.Id, r => r.Id == createFFmpegProfile.ResolutionId, cancellationToken)
            .MapT(r => r.Id)
            .Map(o => o.ToValidation(
                BaseError.BadRequest($"[Resolution] {createFFmpegProfile.ResolutionId} does not exist")));
}
