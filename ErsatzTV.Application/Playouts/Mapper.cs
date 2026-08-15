using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;

namespace ErsatzTV.Application.Playouts;

internal static class Mapper
{
    internal static PlayoutNameViewModel ProjectToViewModel(Playout playout) =>
        new(
            playout.Id,
            playout.ScheduleKind,
            playout.Channel.Name,
            playout.Channel.Number,
            playout.Channel.PlayoutMode,
            playout.ProgramScheduleId == null ? string.Empty : playout.ProgramSchedule.Name,
            playout.ScheduleFile,
            playout.DailyRebuildTime,
            playout.BuildStatus);

    internal static PlayoutItemViewModel ProjectToViewModel(PlayoutItem playoutItem) =>
        new(
            GetDisplayTitle(playoutItem.MediaItem, playoutItem.ChapterTitle),
            playoutItem.StartOffset,
            playoutItem.FinishOffset,
            playoutItem.GetDisplayDuration(),
            playoutItem.SchedulingContext,
            Some(playoutItem.FillerKind));

    internal static PlayoutAlternateScheduleViewModel ProjectToViewModel(
        ProgramScheduleAlternate programScheduleAlternate) =>
        new(
            programScheduleAlternate.Id,
            programScheduleAlternate.Index,
            programScheduleAlternate.ProgramScheduleId,
            programScheduleAlternate.DaysOfWeek,
            programScheduleAlternate.DaysOfMonth,
            programScheduleAlternate.MonthsOfYear,
            programScheduleAlternate.LimitToDateRange,
            programScheduleAlternate.StartMonth,
            programScheduleAlternate.StartDay,
            programScheduleAlternate.StartYear,
            programScheduleAlternate.EndMonth,
            programScheduleAlternate.EndDay,
            programScheduleAlternate.EndYear);

    internal static PlayoutHistoryViewModel ProjectToViewModel(PlayoutHistory playoutHistory) =>
        new(
            playoutHistory.Id,
            new DateTimeOffset(playoutHistory.When, TimeSpan.Zero).ToLocalTime(),
            new DateTimeOffset(playoutHistory.Finish, TimeSpan.Zero).ToLocalTime(),
            playoutHistory.Key,
            playoutHistory.Details);

    internal static string GetDisplayTitle(MediaItem mediaItem, Option<string> maybeChapterTitle)
    {
        string chapterTitle = maybeChapterTitle.IfNone(string.Empty);

        switch (mediaItem)
        {
            case Episode e:
                string showTitle = e.Season.Show.ShowMetadata.HeadOrNone()
                    .Map(sm => $"{sm.Title} - ").IfNone(string.Empty);
                var episodeNumbers = e.EpisodeMetadata.Map(em => em.EpisodeNumber).ToList();
                var episodeTitles = e.EpisodeMetadata.Map(em => em.Title).ToList();
                if (episodeNumbers.Count == 0 || episodeTitles.Count == 0)
                {
                    return "[unknown episode]";
                }

                var numbersString = $"e{string.Join('e', episodeNumbers.Map(n => $"{n:00}"))}";
                var titlesString = $"{string.Join('/', episodeTitles)}";
                if (!string.IsNullOrWhiteSpace(chapterTitle))
                {
                    titlesString += $" ({chapterTitle})";
                }

                return $"{showTitle}s{e.Season.SeasonNumber:00}{numbersString} - {titlesString}";
            case Movie m:
                return m.MovieMetadata.HeadOrNone().Map(mm => mm.Title).IfNone("[unknown movie]");
            case MusicVideo mv:
                string artistName = mv.Artist.ArtistMetadata.HeadOrNone()
                    .Map(am => $"{am.Title} - ").IfNone(string.Empty);
                return mv.MusicVideoMetadata.HeadOrNone()
                    .Map(mvm => $"{artistName}{mvm.Title}")
                    .Map(s => string.IsNullOrWhiteSpace(chapterTitle)
                        ? s
                        : $"{s} ({chapterTitle})")
                    .IfNone("[unknown music video]");
            case OtherVideo ov:
                return ov.OtherVideoMetadata.HeadOrNone()
                    .Map(ovm => ovm.Title ?? string.Empty)
                    .Map(s => string.IsNullOrWhiteSpace(chapterTitle)
                        ? s
                        : $"{s} ({chapterTitle})")
                    .IfNone("[unknown video]");
            case Song s:
                string songArtist = s.SongMetadata.HeadOrNone()
                    .Map(sm => $"{string.Join(", ", sm.Artists)} - ")
                    .IfNone(string.Empty);
                return s.SongMetadata.HeadOrNone()
                    .Map(sm => $"{songArtist}{sm.Title ?? string.Empty}")
                    .Map(t => string.IsNullOrWhiteSpace(chapterTitle)
                        ? t
                        : $"{s} ({chapterTitle})")
                    .IfNone("[unknown song]");
            case Image i:
                return i.ImageMetadata.HeadOrNone().Map(im => im.Title ?? string.Empty).IfNone("[unknown image]");
            case RemoteStream rs:
                return rs.RemoteStreamMetadata.HeadOrNone().Map(im => im.Title ?? string.Empty)
                    .IfNone("[unknown remote stream]");
            default:
                return string.Empty;
        }
    }

    internal static Option<PlayoutModeViewModel> ProjectToModeViewModel(Option<Channel> maybeChannel)
    {
        foreach (Channel channel in maybeChannel)
        {
            foreach (Playout playout in channel.Playouts.HeadOrNone())
            {
                return new PlayoutModeViewModel(playout.Id, channel.PlayoutMode);
            }
        }

        return Option<PlayoutModeViewModel>.None;
    }
}
