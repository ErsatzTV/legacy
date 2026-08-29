using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public class YamlPlayoutShuffleSequenceHandler : IYamlPlayoutHandler
{
    public bool Reset => false;

    public Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (instruction is not YamlPlayoutShuffleSequenceInstruction shuffleSequenceInstruction)
        {
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(shuffleSequenceInstruction.ShuffleSequence))
        {
            logger.LogWarning("Sequence key is required to shuffle sequence");
            return Task.FromResult(false);
        }

        string sequenceKey = shuffleSequenceInstruction.ShuffleSequence;

        List<YamlPlayoutInstruction> playout = context.CurrentInstructions;

        var groupedSequenceItems = playout
            .Select((instruction, index) => new { Instruction = instruction, Index = index })
            .Where(x => x.Instruction.SequenceKey == sequenceKey)
            .GroupBy(x => x.Instruction.SequenceGuid)
            .ToList();

        foreach (var grouping in groupedSequenceItems)
        {
            var currentGroup = grouping.OrderBy(x => x.Index).ToList();

            // shuffle, avoiding starting with the tail of the last shuffle
            YamlPlayoutInstruction tail = currentGroup.Last().Instruction;
            var shuffledGroup = currentGroup.Select(x => x.Instruction).OrderBy(_ => Guid.NewGuid()).ToList();
            while (shuffledGroup.Count > 1 && shuffledGroup.Head() == tail)
            {
                shuffledGroup = currentGroup.Select(x => x.Instruction).OrderBy(_ => Guid.NewGuid()).ToList();
            }

            for (var index = 0; index < currentGroup.Count; index++)
            {
                shuffledGroup[index].SequenceShuffled = true;
                playout[currentGroup[index].Index] = shuffledGroup[index];
            }
        }

        return Task.FromResult(true);
    }
}
