using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Plex;
using Newtonsoft.Json;

namespace ErsatzTV.Infrastructure.Plex;

public class PlexSecretStore : IPlexSecretStore
{
    public Task<string> GetClientIdentifier() =>
        ReadSecrets().Bind(plexSecrets => Optional(plexSecrets.ClientIdentifier).Match(
            Task.FromResult,
            async () =>
            {
                string identifier = GenerateClientIdentifier();
                plexSecrets.ClientIdentifier = identifier;
                await SaveSecrets(plexSecrets);
                return identifier;
            }));

    public Task<Unit> UpsertClientIdentifier(string clientIdentifier) =>
        ReadSecrets().Bind(secrets =>
        {
            secrets.ClientIdentifier = clientIdentifier;
            return SaveSecrets(secrets);
        });

    public Task<List<PlexUserAuthToken>> GetUserAuthTokens() =>
        ReadSecrets().Map(s => Optional(s.UserAuthTokens).Match(
            tokens => tokens.Map(kvp => new PlexUserAuthToken(kvp.Key, kvp.Value)).ToList(),
            () => new List<PlexUserAuthToken>()));

    public Task<Option<PlexServerAuthToken>> GetServerAuthToken(string clientIdentifier) =>
        ReadSecrets().Map(s => Optional(s.ServerAuthTokens.SingleOrDefault(kvp => kvp.Key == clientIdentifier))
            .Map(kvp => new PlexServerAuthToken(kvp.Key, kvp.Value)));

    public Task<Unit> UpsertUserAuthToken(PlexUserAuthToken userAuthToken) =>
        ReadSecrets().Bind(secrets =>
        {
            secrets.UserAuthTokens ??= new Dictionary<string, string>();
            secrets.UserAuthTokens[userAuthToken.Email] = userAuthToken.AuthToken;
            return SaveSecrets(secrets);
        });

    public Task<Unit> UpsertServerAuthToken(PlexServerAuthToken serverAuthToken) =>
        ReadSecrets().Bind(secrets =>
        {
            secrets.ServerAuthTokens ??= new Dictionary<string, string>();
            secrets.ServerAuthTokens[serverAuthToken.ClientIdentifier] = serverAuthToken.AuthToken;
            return SaveSecrets(secrets);
        });

    public Task<Unit> DeleteAll() =>
        ReadSecrets().Bind(secrets => SaveSecrets(new PlexSecrets { ClientIdentifier = secrets.ClientIdentifier }));

    private static Task<PlexSecrets> ReadSecrets() =>
        File.ReadAllTextAsync(FileSystemLayout.PlexSecretsPath)
            .Map(JsonConvert.DeserializeObject<PlexSecrets>)
            .Map(s => Optional(s).IfNone(new PlexSecrets()))
            .Map(s =>
            {
                s.ServerAuthTokens ??= new Dictionary<string, string>();
                s.UserAuthTokens ??= new Dictionary<string, string>();
                return s;
            });

    private static Task<Unit> SaveSecrets(PlexSecrets plexSecrets) =>
        Some(JsonConvert.SerializeObject(plexSecrets)).Match(
            s => File.WriteAllTextAsync(FileSystemLayout.PlexSecretsPath, s).ToUnit(),
            Task.FromResult(Unit.Default));

    public string GenerateClientIdentifier() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .TrimEnd('=')
            .Replace("/", "_")
            .Replace("+", "-");
}
