using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Globalization;
using Serilog.Formatting.Display;

namespace ErsatzTV.Core;

public class InMemorySink : ILogEventSink
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _logs = new();

    private static readonly MessageTemplateTextFormatter Formatter = new(
        "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}",
        CultureInfo.InvariantCulture);

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue(InMemoryLogService.CorrelationIdKey, out var correlationIdValue) &&
            correlationIdValue is ScalarValue { Value: Guid correlationId })
        {
            ConcurrentQueue<string> logQueue = _logs.GetOrAdd(correlationId, _ => new ConcurrentQueue<string>());

            using (var writer = new StringWriter())
            {
                Formatter.Format(logEvent, writer);
                logQueue.Enqueue(writer.ToString().TrimEnd());
            }

            while (logQueue.Count > 100)
            {
                logQueue.TryDequeue(out _);
            }
        }
    }

    public IEnumerable<string> GetLogs(Guid correlationId)
    {
        _logs.TryGetValue(correlationId, out ConcurrentQueue<string> logs);
        return logs ?? Enumerable.Empty<string>();
    }

    public void ClearLogs(Guid correlationId)
    {
        _logs.TryRemove(correlationId, out _);
    }
}

public class InMemoryLogService
{
    public InMemorySink Sink { get; } = new();

    public static readonly string CorrelationIdKey = "CorrelationId";
}
