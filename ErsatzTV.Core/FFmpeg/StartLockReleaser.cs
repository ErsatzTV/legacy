namespace ErsatzTV.Core.FFmpeg;

public sealed class StartLockReleaser(SemaphoreSlim slim) : IDisposable
{
    private bool _disposedValue;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                slim.Release();
            }

            _disposedValue = true;
        }
    }
}
