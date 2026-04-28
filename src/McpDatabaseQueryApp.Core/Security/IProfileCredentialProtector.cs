using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Security;

/// <summary>
/// Profile-scoped variant of <see cref="ICredentialProtector"/>. Each profile
/// has its own AEAD key (see <see cref="IProfileKeyProvider"/>); ciphertexts
/// produced under one profile cannot be decrypted under another.
/// </summary>
public interface IProfileCredentialProtector
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under the AEAD key derived for
    /// <paramref name="profileId"/>.
    /// </summary>
    (byte[] Cipher, byte[] Nonce) Protect(ProfileId profileId, string plaintext);

    /// <summary>
    /// Decrypts the (<paramref name="cipher"/>, <paramref name="nonce"/>) pair
    /// using the AEAD key derived for <paramref name="profileId"/>. Throws if
    /// the tag does not verify (e.g. the blob was produced under a different
    /// profile).
    /// </summary>
    string Unprotect(ProfileId profileId, byte[] cipher, byte[] nonce);
}
