using System.Security.Cryptography;
using System.Text;
using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Security;

/// <summary>
/// HKDF-SHA256 implementation of <see cref="IProfileKeyProvider"/>. Mixes the
/// server-wide master key with a per-profile <c>info</c> string and a
/// SHA-256-of-id salt to produce 32 bytes of key material per profile.
/// </summary>
public sealed class HkdfProfileKeyProvider : IProfileKeyProvider
{
    private const string InfoPrefix = "mcp-database-query-app/profile/";

    private readonly IMasterKeyProvider _master;

    /// <summary>
    /// Creates a new <see cref="HkdfProfileKeyProvider"/>.
    /// </summary>
    public HkdfProfileKeyProvider(IMasterKeyProvider master)
    {
        ArgumentNullException.ThrowIfNull(master);
        _master = master;
    }

    /// <inheritdoc/>
    public byte[] DeriveKey(ProfileId profileId)
    {
        var ikm = _master.GetKey();
        try
        {
            if (ikm.Length != 32)
            {
                throw new InvalidOperationException("Master key must be 32 bytes (AES-256).");
            }

            var salt = SHA256.HashData(Encoding.UTF8.GetBytes(profileId.Value));
            var info = Encoding.UTF8.GetBytes(InfoPrefix + profileId.Value);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, outputLength: 32, salt: salt, info: info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }
}
