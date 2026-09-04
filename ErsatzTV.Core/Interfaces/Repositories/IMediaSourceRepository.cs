using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Repositories;

public interface IMediaSourceRepository
{
    Task<PlexMediaSource> Add(PlexMediaSource plexMediaSource);
    Task<List<PlexMediaSource>> GetAllPlex();
    Task<List<PlexPathReplacement>> GetPlexPathReplacements(int plexMediaSourceId);
    Task<Option<PlexLibrary>> GetPlexLibrary(int plexLibraryId);
    Task<Option<PlexMediaSource>> GetPlex(int id, CancellationToken cancellationToken);
    Task<Option<PlexMediaSource>> GetPlexByLibraryId(int plexLibraryId);

    Task<List<PlexPathReplacement>> GetPlexPathReplacementsByLibraryId(
        int plexLibraryPathId,
        CancellationToken cancellationToken);

    Task Update(
        PlexMediaSource plexMediaSource,
        List<PlexConnection> toAdd,
        List<PlexConnection> toDelete);

    Task<List<int>> UpdateLibraries(
        int plexMediaSourceId,
        List<PlexLibrary> toAdd,
        List<PlexLibrary> toDelete,
        List<PlexLibrary> toUpdate,
        CancellationToken cancellationToken);

    Task<List<int>> UpdateLibraries(
        int jellyfinMediaSourceId,
        List<JellyfinLibrary> toAdd,
        List<JellyfinLibrary> toDelete,
        List<JellyfinLibrary> toUpdate,
        CancellationToken cancellationToken);

    Task<List<int>> UpdateLibraries(
        int embyMediaSourceId,
        List<EmbyLibrary> toAdd,
        List<EmbyLibrary> toDelete,
        List<EmbyLibrary> toUpdate,
        CancellationToken cancellationToken);

    Task<Unit> UpdatePathReplacements(
        int plexMediaSourceId,
        List<PlexPathReplacement> toAdd,
        List<PlexPathReplacement> toUpdate,
        List<PlexPathReplacement> toDelete);

    Task<List<int>> DeleteAllPlex();
    Task<List<int>> DeletePlex(PlexMediaSource plexMediaSource);
    Task<List<int>> DisablePlexLibrarySync(List<int> libraryIds);
    Task EnablePlexLibrarySync(IEnumerable<int> libraryIds);

    Task<Unit> UpsertJellyfin(string address, string serverName, string operatingSystem);
    Task<List<JellyfinMediaSource>> GetAllJellyfin(CancellationToken cancellationToken);
    Task<Option<JellyfinMediaSource>> GetJellyfin(int id);
    Task<List<JellyfinLibrary>> GetJellyfinLibraries(int jellyfinMediaSourceId);
    Task<Unit> EnableJellyfinLibrarySync(IEnumerable<int> libraryIds);
    Task<List<int>> DisableJellyfinLibrarySync(List<int> libraryIds);
    Task<Option<JellyfinLibrary>> GetJellyfinLibrary(int jellyfinLibraryId);
    Task<Option<JellyfinMediaSource>> GetJellyfinByLibraryId(int jellyfinLibraryId);
    Task<List<JellyfinPathReplacement>> GetJellyfinPathReplacements(int jellyfinMediaSourceId);

    Task<List<JellyfinPathReplacement>> GetJellyfinPathReplacementsByLibraryId(
        int jellyfinLibraryPathId,
        CancellationToken cancellationToken);

    Task<Unit> UpdatePathReplacements(
        int jellyfinMediaSourceId,
        List<JellyfinPathReplacement> toAdd,
        List<JellyfinPathReplacement> toUpdate,
        List<JellyfinPathReplacement> toDelete);

    Task<List<int>> DeleteAllJellyfin();

    Task<Unit> UpsertEmby(string address, string serverName, string operatingSystem);
    Task<List<EmbyMediaSource>> GetAllEmby(CancellationToken cancellationToken);
    Task<Option<EmbyMediaSource>> GetEmby(int id, CancellationToken cancellationToken);
    Task<Option<EmbyMediaSource>> GetEmbyByLibraryId(int embyLibraryId);
    Task<Option<EmbyLibrary>> GetEmbyLibrary(int embyLibraryId, CancellationToken cancellationToken);
    Task<List<EmbyLibrary>> GetEmbyLibraries(int embyMediaSourceId);
    Task<List<EmbyPathReplacement>> GetEmbyPathReplacements(int embyMediaSourceId);

    Task<List<EmbyPathReplacement>> GetEmbyPathReplacementsByLibraryId(
        int embyLibraryPathId,
        CancellationToken cancellationToken);

    Task<Unit> UpdatePathReplacements(
        int embyMediaSourceId,
        List<EmbyPathReplacement> toAdd,
        List<EmbyPathReplacement> toUpdate,
        List<EmbyPathReplacement> toDelete);

    Task<List<int>> DeleteAllEmby();
    Task<Unit> EnableEmbyLibrarySync(IEnumerable<int> libraryIds);
    Task<List<int>> DisableEmbyLibrarySync(List<int> libraryIds);
    Task<Unit> UpdateLastCollectionScan(EmbyMediaSource embyMediaSource);
    Task<Unit> UpdateLastCollectionScan(JellyfinMediaSource jellyfinMediaSource);
    Task<Unit> UpdateLastCollectionScan(PlexMediaSource plexMediaSource);

    Task<Unit> SetPlexMissingSince(int plexMediaSourceId, DateTime? missingSince);
}
