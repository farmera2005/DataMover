using Lakeflow.Agent.Storage;
using Lakeflow.Connectors.SqlServer;
using Lakeflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Lakeflow.Agent.Capture;

public interface IConnectorFactory
{
    ISourceConnector Create(SourceConfig source);
}

/// <summary>Source passwords from the agent's local store (DPAPI).</summary>
public sealed class StoreCredentials(LocalStore store) : ICredentialStore
{
    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken) => Task.FromResult(store.GetCredential(name));
}

public sealed class ConnectorFactory(ICredentialStore credentials, ILoggerFactory loggers) : IConnectorFactory
{
    public ISourceConnector Create(SourceConfig source) => source.Engine switch
    {
        SourceEngine.SqlServer => new SqlServerConnector(source, credentials, loggers.CreateLogger<SqlServerConnector>()),
        _ => throw new CaptureUnavailableException($"No {source.Engine} connector in this agent version."),
    };
}
