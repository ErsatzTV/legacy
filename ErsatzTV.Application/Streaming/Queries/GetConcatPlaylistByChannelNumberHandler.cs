using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Streaming;

public class
    GetConcatPlaylistByChannelNumberHandler : IRequestHandler<GetConcatPlaylistByChannelNumber,
    Either<BaseError, ConcatPlaylist>>
{
    private readonly IChannelRepository _channelRepository;

    public GetConcatPlaylistByChannelNumberHandler(IChannelRepository channelRepository) =>
        _channelRepository = channelRepository;

    public Task<Either<BaseError, ConcatPlaylist>> Handle(
        GetConcatPlaylistByChannelNumber request,
        CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(channel => new ConcatPlaylist(request.Scheme, request.Host, channel.Number, request.Mode))
            .Map(v => v.ToEither<ConcatPlaylist>());

    private Task<Validation<BaseError, Channel>> Validate(GetConcatPlaylistByChannelNumber request) =>
        ChannelMustExist(request);

    private async Task<Validation<BaseError, Channel>> ChannelMustExist(GetConcatPlaylistByChannelNumber request) =>
        (await _channelRepository.GetByNumber(request.ChannelNumber))
        .ToValidation(BaseError.NotFound($"Channel number {request.ChannelNumber} does not exist."));
}
