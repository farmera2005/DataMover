using Lakeflow.Contracts;

namespace Lakeflow.Testing;

public sealed class InMemoryCredentialStore(IReadOnlyDictionary<string, string> secrets) : ICredentialStore
{
    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(secrets.GetValueOrDefault(name));
}
