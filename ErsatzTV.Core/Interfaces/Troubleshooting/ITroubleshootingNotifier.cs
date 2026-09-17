namespace ErsatzTV.Core.Interfaces.Troubleshooting;

public interface ITroubleshootingNotifier
{
    bool IsFailed(Guid sessionId);

    void NotifyFailed(Guid sessionId);

    bool IsCompleted(Guid sessionId);

    void NotifyCompleted(Guid sessionId);

    void RemoveSession(Guid sessionId);
}
