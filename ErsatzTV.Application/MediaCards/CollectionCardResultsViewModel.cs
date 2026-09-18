namespace ErsatzTV.Application.MediaCards;

public record CollectionCardResultsViewModel(
    string Name,
    List<MovieCardViewModel> MovieCards,
    List<TelevisionShowCardViewModel> ShowCards,
    List<TelevisionSeasonCardViewModel> SeasonCards,
    List<TelevisionEpisodeCardViewModel> EpisodeCards,
    List<ArtistCardViewModel> ArtistCards,
    List<MusicVideoCardViewModel> MusicVideoCards,
    List<OtherVideoCardViewModel> OtherVideoCards,
    List<SongCardViewModel> SongCards,
    List<ImageCardViewModel> ImageCards,
    List<RemoteStreamCardViewModel> RemoteStreamCards)
{
    public bool UseCustomPlaybackOrder { get; set; }

    public bool SupportsCustomOrdering => MovieCards.Count > 0 && ShowCards.Count == 0 && SeasonCards.Count == 0 &&
                                          EpisodeCards.Count == 0 && ArtistCards.Count == 0
                                          && MusicVideoCards.Count == 0 && OtherVideoCards.Count == 0 &&
                                          SongCards.Count == 0 && ImageCards.Count == 0 && RemoteStreamCards.Count == 0;
}
