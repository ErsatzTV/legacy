namespace ErsatzTV.Application.Playouts;

public record GetPlayoutModeByChannelNumber(string ChannelNumber) : IRequest<Option<PlayoutModeViewModel>>;
