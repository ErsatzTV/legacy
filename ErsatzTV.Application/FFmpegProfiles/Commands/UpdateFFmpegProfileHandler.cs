using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.FFmpeg.Preset;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.FFmpegProfiles;

public class UpdateFFmpegProfileHandler(IDbContextFactory<TvContext> dbContextFactory, ISearchTargets searchTargets)
    : IRequestHandler<UpdateFFmpegProfile, Either<BaseError, UpdateFFmpegProfileResult>>
{
    public async Task<Either<BaseError, UpdateFFmpegProfileResult>> Handle(
        UpdateFFmpegProfile request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, FFmpegProfile> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(p => ApplyUpdateRequest(dbContext, p, request, cancellationToken));
    }

    private async Task<UpdateFFmpegProfileResult> ApplyUpdateRequest(
        TvContext dbContext,
        FFmpegProfile p,
        UpdateFFmpegProfile update,
        CancellationToken cancellationToken)
    {
        var hwAccel = update.NormalizeVideo
            ? update.HardwareAcceleration
            : HardwareAccelerationKind.None;

        p.Name = update.Name;
        p.ThreadCount = update.ThreadCount;
        p.NormalizeAudio = update.NormalizeAudio;
        p.NormalizeVideo = update.NormalizeVideo;
        p.HardwareAcceleration = hwAccel;
        p.VaapiDisplay = update.VaapiDisplay;
        p.VaapiDriver = update.VaapiDriver;
        p.VaapiDevice = update.VaapiDevice;
        p.QsvExtraHardwareFrames = update.QsvExtraHardwareFrames;
        p.ResolutionId = update.ResolutionId;
        p.ScalingBehavior = update.ScalingBehavior;
        p.PadMode = update.PadMode;
        p.VideoFormat = update.NormalizeVideo ? update.VideoFormat : FFmpegProfileVideoFormat.Copy;
        p.VideoProfile = update.VideoProfile;
        p.VideoPreset = update.VideoPreset;
        p.AllowBFrames = update.AllowBFrames;

        // mpeg2video only supports 8-bit content
        p.BitDepth = update.VideoFormat is FFmpegProfileVideoFormat.Mpeg2Video
            ? FFmpegProfileBitDepth.EightBit
            : update.BitDepth;

        if (p.HardwareAcceleration is not (HardwareAccelerationKind.Nvenc or HardwareAccelerationKind.Vaapi
                or HardwareAccelerationKind.Qsv) &&
            p.VideoFormat is FFmpegProfileVideoFormat.Av1)
        {
            p.VideoFormat = FFmpegProfileVideoFormat.Hevc;
        }

        // only allow customization with VAAPI accel
        if (p.HardwareAcceleration is HardwareAccelerationKind.None)
        {
            p.PadMode = FilterMode.Software;
        }
        else if (p.HardwareAcceleration is not HardwareAccelerationKind.Vaapi)
        {
            p.PadMode = FilterMode.HardwareIfPossible;
        }

        p.VideoBitrate = update.VideoBitrate;
        p.VideoBufferSize = update.VideoBufferSize;
        p.TonemapAlgorithm = update.TonemapAlgorithm;
        p.AudioFormat = update.NormalizeAudio ? update.AudioFormat : FFmpegProfileAudioFormat.Copy;
        p.AudioBitrate = update.AudioBitrate;
        p.AudioBufferSize = update.AudioBufferSize;

        p.NormalizeLoudnessMode = update.NormalizeLoudnessMode;
        p.TargetLoudness = update.NormalizeLoudnessMode is NormalizeLoudnessMode.LoudNorm
            ? update.TargetLoudness
            : null;

        p.AudioChannels = update.AudioChannels;
        p.AudioSampleRate = update.AudioSampleRate;
        p.NormalizeFramerate = update.NormalizeFramerate;
        p.NormalizeColors = update.NormalizeColors;
        p.DeinterlaceVideo = update.DeinterlaceVideo;

        // don't save invalid preset
        ICollection<string> presets = FFmpegLibraryHelper.PresetsForFFmpegProfile(
            p.HardwareAcceleration,
            p.VideoFormat,
            p.BitDepth);

        if (!presets.Contains(p.VideoPreset))
        {
            p.VideoPreset = VideoPreset.Unset;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        searchTargets.SearchTargetsChanged();

        return new UpdateFFmpegProfileResult(p.Id);
    }

    private static async Task<Validation<BaseError, FFmpegProfile>> Validate(
        TvContext dbContext,
        UpdateFFmpegProfile request,
        CancellationToken cancellationToken) =>
        (await FFmpegProfileMustExist(dbContext, request, cancellationToken),
            await ValidateName(dbContext, request),
            ValidateThreadCount(request),
            await ResolutionMustExist(dbContext, request, cancellationToken))
        .Apply((ffmpegProfileToUpdate, _, _, _) => ffmpegProfileToUpdate);

    private static Task<Validation<BaseError, FFmpegProfile>> FFmpegProfileMustExist(
        TvContext dbContext,
        UpdateFFmpegProfile updateFFmpegProfile,
        CancellationToken cancellationToken) =>
        dbContext.FFmpegProfiles
            .SelectOneAsync(p => p.Id, p => p.Id == updateFFmpegProfile.FFmpegProfileId, cancellationToken)
            .Map(o => o.ToValidation(BaseError.NotFound("FFmpegProfile does not exist.")));

    private static async Task<Validation<BaseError, string>> ValidateName(
        TvContext dbContext,
        UpdateFFmpegProfile updateFFmpegProfile)
    {
        if (updateFFmpegProfile.Name.Length > 50)
        {
            return BaseError.BadRequest($"FFmpeg profile name \"{updateFFmpegProfile.Name}\" is invalid");
        }

        Option<FFmpegProfile> maybeExisting = await dbContext.FFmpegProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(ff =>
                ff.Id != updateFFmpegProfile.FFmpegProfileId && ff.Name == updateFFmpegProfile.Name)
            .Map(Optional);

        return maybeExisting.IsSome
            ? BaseError.Conflict($"An ffmpeg profile named \"{updateFFmpegProfile.Name}\" already exists in the database")
            : Success<BaseError, string>(updateFFmpegProfile.Name);
    }

    private static Validation<BaseError, int> ValidateThreadCount(UpdateFFmpegProfile updateFFmpegProfile) =>
        updateFFmpegProfile.AtLeast(0)(p => p.ThreadCount);

    private static Task<Validation<BaseError, int>> ResolutionMustExist(
        TvContext dbContext,
        UpdateFFmpegProfile updateFFmpegProfile,
        CancellationToken cancellationToken) =>
        dbContext.Resolutions
            .SelectOneAsync(r => r.Id, r => r.Id == updateFFmpegProfile.ResolutionId, cancellationToken)
            .MapT(r => r.Id)
            .Map(o => o.ToValidation(
                BaseError.BadRequest($"[Resolution] {updateFFmpegProfile.ResolutionId} does not exist")));
}
