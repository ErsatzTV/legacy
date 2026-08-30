namespace ErsatzTV.Core.Interfaces.Scheduling;

public interface IPlayoutGapInserter
{
    Task InsertGaps(int playoutId, CancellationToken cancellationToken);
}
