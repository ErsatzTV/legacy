namespace ErsatzTV.Core.FFmpeg;

public sealed class SessionEndedBeforeReady(string channelNumber)
    : BaseError($"Session for channel {channelNumber} ended before the playlist was ready");
