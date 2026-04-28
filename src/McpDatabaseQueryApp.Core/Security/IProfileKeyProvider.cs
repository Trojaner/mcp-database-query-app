using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Security;

/// <summary>
/// Derives a 32-byte AES-256-GCM key for a specific <see cref="ProfileId"/>
/// from the server-wide master key. Keys are deterministic for a given
/// (master key, profile id) pair, so a profile's stored ciphertexts remain
/// decryptable across process restarts.
/// </summary>
/// <remarks>
/// <para><strong>Threat model.</strong> The server's master key (resolved via
/// <see cref="IMasterKeyProvider"/> from <c>McpDatabaseQueryApp:Secrets:KeyRef</c>)
/// remains the trust root: an attacker who steals the master key can derive every
/// profile key and decrypt every profile's stored credentials. Per-profile
/// derivation does NOT defend against master-key compromise.</para>
/// <para>What it does buy:</para>
/// <list type="bullet">
///   <item><description>
///   Blast-radius reduction: a fault that leaks a single profile's derived key
///   (e.g. logging, memory dumps scoped to one request) does not compromise
///   other profiles' ciphertexts.
///   </description></item>
///   <item><description>
///   Cryptographic isolation between tenants: profile A's protector cannot
///   decrypt profile B's blobs even if a bug crosses the profile filter in the
///   storage layer, because the AEAD tag will fail to verify.
///   </description></item>
///   <item><description>
///   Future per-profile key rotation: a profile id can be reassigned a new
///   underlying key (by changing the salt or info string) without touching
///   the master key.
///   </description></item>
/// </list>
/// <para>The user spec required per-profile encryption; this is its concrete
/// realisation. We deliberately do not oversell it as a master-key replacement.</para>
/// </remarks>
public interface IProfileKeyProvider
{
    /// <summary>
    /// Derives a 32-byte AEAD key for the given <paramref name="profileId"/>.
    /// Caller takes ownership of the buffer and is responsible for zeroing
    /// it when it is no longer needed.
    /// </summary>
    byte[] DeriveKey(ProfileId profileId);
}
