namespace Lakeflow.Contracts;

/// <summary>
/// Source passwords held on the agent (DPAPI, machine scope), looked up by <see cref="ConnectionSettings.CredentialName"/>.
/// Implementations never log the value.
/// </summary>
public interface ICredentialStore
{
    /// <returns>The secret, or null when no credential of that name is stored.</returns>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);
}

/// <summary>The source cannot be captured as configured (e.g. Change Tracking is not enabled on the table).</summary>
public sealed class CaptureUnavailableException(string message) : Exception(message);
