using System.Collections.Concurrent;
using ErsatzTV.Core.Interfaces.Troubleshooting;

namespace ErsatzTV.Core.Troubleshooting;

public class TroubleshootingNotifier : ITroubleshootingNotifier
{
    private readonly ConcurrentDictionary<Guid, bool> _completedSessions = new();
    private readonly ConcurrentDictionary<Guid, bool> _failedSessions = new();

    public bool IsFailed(Guid sessionId) => _failedSessions.TryGetValue(sessionId, out _);

    public void NotifyFailed(Guid sessionId) => _failedSessions[sessionId] = true;

    public bool IsCompleted(Guid sessionId) => _completedSessions.TryGetValue(sessionId, out _);

    public void NotifyCompleted(Guid sessionId) => _completedSessions[sessionId] = true;

    public void RemoveSession(Guid sessionId)
    {
        _failedSessions.TryRemove(sessionId, out _);
        _completedSessions.TryRemove(sessionId, out _);
    }
}
