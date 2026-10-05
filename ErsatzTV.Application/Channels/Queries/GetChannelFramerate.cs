using ErsatzTV.FFmpeg;

namespace ErsatzTV.Application.Channels;

public record GetChannelFramerate(string ChannelNumber, bool IgnoreProfile = false) : IRequest<Option<FrameRate>>;
