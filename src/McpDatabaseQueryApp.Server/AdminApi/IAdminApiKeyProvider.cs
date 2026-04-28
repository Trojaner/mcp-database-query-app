namespace McpDatabaseQueryApp.Server.AdminApi;

/// <summary>
/// Resolves the admin API key once at startup and exposes it as raw bytes for
/// constant-time comparison on every request. The interface intentionally
/// returns bytes (not a <see cref="string"/>) to avoid intern-table leaks and
/// to integrate with
/// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>.
/// </summary>
public interface IAdminApiKeyProvider
{
    /// <summary>
    /// Returns true and emits the admin key bytes when a key is configured.
    /// Returns false when no key is configured (the auth handler treats this
    /// as a configuration error and refuses every request).
    /// </summary>
    bool TryGetKeyBytes(out ReadOnlySpan<byte> keyBytes);

    /// <summary>
    /// Number of bytes (UTF-8) the configured key occupies. Used at startup to
    /// log a non-sensitive summary; <c>0</c> when no key is configured.
    /// </summary>
    int KeyByteLength { get; }

    /// <summary>
    /// Masked prefix of the configured key, suitable for log lines. Returns
    /// <c>null</c> when no key is configured.
    /// </summary>
    string? MaskedPrefix { get; }
}
