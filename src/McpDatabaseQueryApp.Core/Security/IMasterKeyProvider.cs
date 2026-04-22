using System.Security.Cryptography;
using System.Text;
using McpDatabaseQueryApp.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace McpDatabaseQueryApp.Core.Security;

public interface IMasterKeyProvider
{
    byte[] GetKey();
}

public sealed class ConfiguredMasterKeyProvider : IMasterKeyProvider
{
    private const string Salt = "mcp-database-query-app:v1:aead";

    private readonly string _keyRef;
    private readonly IConfiguration _configuration;

    public ConfiguredMasterKeyProvider(SecretsOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keyRef = options.KeyRef;
        _configuration = configuration;
    }

    public byte[] GetKey()
    {
        var material = ResolveMaterial();
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(material),
            outputLength: 32,
            salt: Encoding.UTF8.GetBytes(Salt),
            info: Encoding.UTF8.GetBytes("credential-protection"));
    }

    private string ResolveMaterial()
    {
        if (string.IsNullOrWhiteSpace(_keyRef))
        {
            throw new InvalidOperationException("McpDatabaseQueryApp:Secrets:KeyRef is not configured.");
        }

        var colon = _keyRef.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw new InvalidOperationException($"Invalid KeyRef format: '{_keyRef}'. Expected 'Scheme:Path'.");
        }

        var scheme = _keyRef[..colon];
        var value = _keyRef[(colon + 1)..];

        return scheme.ToUpperInvariant() switch
        {
            "ENV" => Environment.GetEnvironmentVariable(value)
                ?? throw new InvalidOperationException($"Environment variable '{value}' is not set."),
            "FILE" => File.ReadAllText(PathResolver.Resolve(value)).Trim(),
            "USERSECRETS" => _configuration[value]
                ?? throw new InvalidOperationException($"User secret '{value}' is not set. Run 'dotnet user-secrets set {value} <value>'."),
            "CONFIG" => _configuration[value]
                ?? throw new InvalidOperationException($"Configuration value '{value}' is not set."),
            "LITERAL" => value,
            _ => throw new InvalidOperationException($"Unknown KeyRef scheme '{scheme}'.")
        };
    }
}

public sealed class EphemeralMasterKeyProvider : IMasterKeyProvider
{
    private readonly byte[] _key;

    public EphemeralMasterKeyProvider()
    {
        _key = RandomNumberGenerator.GetBytes(32);
    }

    public byte[] GetKey() => _key.ToArray();
}
