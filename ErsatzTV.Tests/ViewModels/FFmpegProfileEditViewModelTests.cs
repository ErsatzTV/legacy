using ErsatzTV.Core.Domain;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.Validators;
using ErsatzTV.ViewModels;
using FluentValidation.Results;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.ViewModels;

[TestFixture]
public class FFmpegProfileEditViewModelTests
{
    private static readonly string?[] StoredProfiles =
        [VideoProfile.Main, VideoProfile.High, VideoProfile.High10, VideoProfile.High444p, string.Empty, null];

    [Test]
    public void SyncHiddenVideoProfile_Should_Satisfy_Validator(
        [Values] HardwareAccelerationKind accel,
        [Values(FFmpegProfileBitDepth.EightBit, FFmpegProfileBitDepth.TenBit)] FFmpegProfileBitDepth bitDepth,
        [ValueSource(nameof(StoredProfiles))] string storedProfile)
    {
        var model = new FFmpegProfileEditViewModel
        {
            NormalizeVideo = true,
            HardwareAcceleration = accel,
            VideoFormat = FFmpegProfileVideoFormat.H264,
            BitDepth = bitDepth,
            VideoProfile = storedProfile
        };

        model.SyncHiddenVideoProfile();

        ValidationResult result = new FFmpegProfileEditViewModelValidator().Validate(model);
        result.Errors.Where(e => e.PropertyName == nameof(FFmpegProfileEditViewModel.VideoProfile)).ShouldBeEmpty();
    }

    [Test]
    public void SyncHiddenVideoProfile_Should_Keep_Valid_8Bit_Profile()
    {
        var model = new FFmpegProfileEditViewModel
        {
            NormalizeVideo = true,
            HardwareAcceleration = HardwareAccelerationKind.Vaapi,
            VideoFormat = FFmpegProfileVideoFormat.H264,
            BitDepth = FFmpegProfileBitDepth.EightBit,
            VideoProfile = VideoProfile.Main
        };

        model.SyncHiddenVideoProfile();

        model.VideoProfile.ShouldBe(VideoProfile.Main);
    }
}
