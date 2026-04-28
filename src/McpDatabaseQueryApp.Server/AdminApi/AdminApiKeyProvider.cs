using System.Text;
using McpDatabaseQueryApp.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.AdminApi;

/// <summary>
/// Default <see cref="IAdminApiKeyProvider"/>. Resolves the configured key
/// either from <see cref="AdminApiOptions.ApiKey"/> (literal) or
/// <see cref="AdminApiOptions.ApiKeyRef"/> (indirection: <c>Env:</c>,
/// <c>File:</c>, <c>UserSecrets:</c>, <c>Config:</c>, or <c>Literal:</c>).
/// </summary>
/// <remarks>
/// The resolved key bytes are cached in memory for the lifetime of the
/// process. The provider intentionally never returns the key as a string
/// after resolution to discourage accidental logging.
/// </remarks>
public sealed class AdminApiKeyProvider : IAdminApiKeyProvider
{
    private readonly byte[]? _keyBytes;

    /// <summary>
    /// Creates a new <see cref="AdminApiKeyProvider"/>. Throws when neither
    /// <see cref="AdminApiOptions.ApiKey"/> nor
    /// <see cref="AdminApiOptions.ApiKeyRef"/> resolves to a non-empty value
    /// AND <see cref="AdminApiOptions.Enabled"/> is true.
    /// </summary>
    public AdminApiKeyProvider(
        AdminApiOptions options,
        IConfiguration configuration,
        ILogger<AdminApiKeyProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        if (!options.Enabled)
        {
            _keyBytes = null;
            return;
        }

        string? resolved = null;
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            logger.LogWarning(
                "AdminApi:ApiKey is set as a literal value in configuration. Prefer AdminApi:ApiKeyRef with an Env/File/UserSecrets indirection so the key is not committed.");
            resolved = options.ApiKey;
        }
        else if (!string.IsNullOrWhiteSpace(options.ApiKeyRef))
        {
            resolved = ResolveKeyRef(options.ApiKeyRef!, configuration);
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                "AdminApi is enabled but no API key is configured. Set McpDatabaseQueryApp:AdminApi:ApiKey or McpDatabaseQueryApp:AdminApi:ApiKeyRef.");
        }

        _keyBytes = Encoding.UTF8.GetBytes(resolved);

        var prefix = BuildMaskedPrefix(resolved);
        MaskedPrefix = prefix;
        logger.LogInformation(
            "AdminApi key loaded: '{MaskedPrefix}' ({Length} chars).",
            prefix,
            resolved.Length);
    }

    /// <inheritdoc />
    public bool TryGetKeyBytes(out ReadOnlySpan<byte> keyBytes)
    {
        if (_keyBytes is null)
        {
            keyBytes = ReadOnlySpan<byte>.Empty;
            return false;
        }

        keyBytes = _keyBytes;
        return true;
    }

    /// <inheritdoc />
    public int KeyByteLength => _keyBytes?.Length ?? 0;

    /// <inheritdoc />
    public string? MaskedPrefix { get; }

    private static string ResolveKeyRef(string keyRef, IConfiguration configuration)
    {
        var colon = keyRef.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw new InvalidOperationException(
                $"Invalid AdminApi:ApiKeyRef format: '{keyRef}'. Expected 'Scheme:Path'.");
        }

        var scheme = keyRef[..colon];
        var value = keyRef[(colon + 1)..];

        return scheme.ToUpperInvariant() switch
        {
            "ENV" => Environment.GetEnvironmentVariable(value)
                ?? throw new InvalidOperationException(
                    $"AdminApi:ApiKeyRef points to environment variable '{value}', which is not set."),
            "FILE" => File.ReadAllText(PathResolver.Resolve(value)).Trim(),
            "USERSECRETS" => configuration[value]
                ?? throw new InvalidOperationException(
                    $"AdminApi:ApiKeyRef points to user secret '{value}', which is not set. Run 'dotnet user-secrets set {value} <value>'."),
            "CONFIG" => configuration[value]
                ?? throw new InvalidOperationException(
                    $"AdminApi:ApiKeyRef points to configuration value '{value}', which is not set."),
            "LITERAL" => value,
            _ => throw new InvalidOperationException(
                $"Unknown AdminApi:ApiKeyRef scheme '{scheme}'."),
        };
    }

    private static string BuildMaskedPrefix(string key)
    {
        if (key.Length <= 3)
        {
            return new string('*', key.Length);
        }

        return string.Concat(key.AsSpan(0, 3), "***");
    }
}
