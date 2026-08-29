using System.Threading.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Plex;

namespace ErsatzTV.Application.Plex;

public class TryCompletePlexPinFlowHandler : IRequestHandler<TryCompletePlexPinFlow, Either<BaseError, bool>>
{
    private readonly ChannelWriter<IPlexBackgroundServiceRequest> _channel;
    private readonly IEntityLocker _entityLocker;
    private readonly IPlexTvApiClient _plexTvApiClient;

    public TryCompletePlexPinFlowHandler(
        IPlexTvApiClient plexTvApiClient,
        ChannelWriter<IPlexBackgroundServiceRequest> channel,
        IEntityLocker entityLocker)
    {
        _plexTvApiClient = plexTvApiClient;
        _channel = channel;
        _entityLocker = entityLocker;
    }

    public async Task<Either<BaseError, bool>>
        Handle(TryCompletePlexPinFlow request, CancellationToken cancellationToken)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
        CancellationToken token = linkedTokenSource.Token;

        // the sign-in flow takes the plex lock in the UI; only a completed sign-in reaches
        // SynchronizePlexMediaSources, which is what releases it, so every other exit unlocks here
        var authenticated = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                bool result = await _plexTvApiClient.TryCompletePinFlow(request.AuthPin);
                if (result)
                {
                    await _channel.WriteAsync(new SynchronizePlexMediaSources(), token);
                    authenticated = true;
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // the two minute window ended without a sign-in
        }
        finally
        {
            if (!authenticated)
            {
                _entityLocker.UnlockPlex();
            }
        }

        return false;
    }
}
