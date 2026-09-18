using System.Collections.Concurrent;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Notifications;
using MediatR;

namespace ErsatzTV.Infrastructure.Locking;

public class EntityLocker(IMediator mediator) : IEntityLocker
{
    private readonly ConcurrentDictionary<int, byte> _lockedLibraries = new();
    private readonly ConcurrentDictionary<int, byte> _lockedPlayouts = new();
    private readonly ConcurrentDictionary<Type, byte> _lockedRemoteMediaSourceTypes = new();
    // 0 = unlocked, 1 = locked; int so Interlocked can flip them atomically
    private int _embyCollections;
    private int _jellyfinCollections;
    private int _plex;
    private int _plexCollections;
    private int _trakt;
    private int _troubleshootingPlayback;

    public event EventHandler OnLibraryChanged;
    public event EventHandler OnPlexChanged;
    public event EventHandler<Type> OnRemoteMediaSourceChanged;
    public event EventHandler OnTraktChanged;
    public event EventHandler OnEmbyCollectionsChanged;
    public event EventHandler OnJellyfinCollectionsChanged;
    public event EventHandler OnPlexCollectionsChanged;
    public event EventHandler OnTroubleshootingPlaybackChanged;

    public bool LockLibrary(int libraryId)
    {
        if (!_lockedLibraries.ContainsKey(libraryId) && _lockedLibraries.TryAdd(libraryId, 0))
        {
            OnLibraryChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool UnlockLibrary(int libraryId)
    {
        if (_lockedLibraries.TryRemove(libraryId, out byte _))
        {
            OnLibraryChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool IsLibraryLocked(int libraryId) =>
        _lockedLibraries.ContainsKey(libraryId);

    public bool LockPlex()
    {
        if (TryLock(ref _plex))
        {
            OnPlexChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool UnlockPlex()
    {
        if (TryUnlock(ref _plex))
        {
            OnPlexChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool IsPlexLocked() => Volatile.Read(ref _plex) == 1;

    public bool IsRemoteMediaSourceLocked<TMediaSource>() =>
        _lockedRemoteMediaSourceTypes.ContainsKey(typeof(TMediaSource));

    public bool LockRemoteMediaSource<TMediaSource>()
    {
        Type mediaSourceType = typeof(TMediaSource);

        if (!_lockedRemoteMediaSourceTypes.ContainsKey(mediaSourceType) &&
            _lockedRemoteMediaSourceTypes.TryAdd(mediaSourceType, 0))
        {
            OnRemoteMediaSourceChanged?.Invoke(this, mediaSourceType);
            return true;
        }

        return false;
    }

    public bool UnlockRemoteMediaSource<TMediaSource>()
    {
        Type mediaSourceType = typeof(TMediaSource);

        if (_lockedRemoteMediaSourceTypes.TryRemove(mediaSourceType, out byte _))
        {
            OnRemoteMediaSourceChanged?.Invoke(this, mediaSourceType);
            return true;
        }

        return false;
    }

    public bool LockTrakt()
    {
        if (TryLock(ref _trakt))
        {
            OnTraktChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool UnlockTrakt()
    {
        if (TryUnlock(ref _trakt))
        {
            OnTraktChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool IsTraktLocked() => Volatile.Read(ref _trakt) == 1;

    public bool LockEmbyCollections()
    {
        if (TryLock(ref _embyCollections))
        {
            OnEmbyCollectionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool UnlockEmbyCollections()
    {
        if (TryUnlock(ref _embyCollections))
        {
            OnEmbyCollectionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool AreEmbyCollectionsLocked() => Volatile.Read(ref _embyCollections) == 1;

    public bool LockJellyfinCollections()
    {
        if (TryLock(ref _jellyfinCollections))
        {
            OnJellyfinCollectionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool UnlockJellyfinCollections()
    {
        if (TryUnlock(ref _jellyfinCollections))
        {
            OnJellyfinCollectionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool AreJellyfinCollectionsLocked() => Volatile.Read(ref _jellyfinCollections) == 1;

    public bool LockPlexCollections()
    {
        if (TryLock(ref _plexCollections))
        {
            OnPlexCollectionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool UnlockPlexCollections()
    {
        if (TryUnlock(ref _plexCollections))
        {
            OnPlexCollectionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool ArePlexCollectionsLocked() => Volatile.Read(ref _plexCollections) == 1;

    public async Task<bool> LockPlayout(int playoutId)
    {
        if (!_lockedPlayouts.ContainsKey(playoutId) && _lockedPlayouts.TryAdd(playoutId, 0))
        {
            await mediator.Publish(new PlayoutUpdatedNotification(playoutId, true));
            return true;
        }

        return false;
    }

    public async Task<bool> UnlockPlayout(int playoutId)
    {
        if (_lockedPlayouts.TryRemove(playoutId, out byte _))
        {
            await mediator.Publish(new PlayoutUpdatedNotification(playoutId, false));
            return true;
        }

        return false;
    }

    public bool IsPlayoutLocked(int playoutId) => _lockedPlayouts.ContainsKey(playoutId);

    public bool LockTroubleshootingPlayback()
    {
        if (TryLock(ref _troubleshootingPlayback))
        {
            OnTroubleshootingPlaybackChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool UnlockTroubleshootingPlayback()
    {
        if (TryUnlock(ref _troubleshootingPlayback))
        {
            OnTroubleshootingPlaybackChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public bool IsTroubleshootingPlaybackLocked() => Volatile.Read(ref _troubleshootingPlayback) == 1;

    private static bool TryLock(ref int flag) => Interlocked.CompareExchange(ref flag, 1, 0) == 0;

    private static bool TryUnlock(ref int flag) => Interlocked.CompareExchange(ref flag, 0, 1) == 1;
}
