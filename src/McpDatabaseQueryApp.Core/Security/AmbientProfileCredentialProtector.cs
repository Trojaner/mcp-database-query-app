using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Security;

/// <summary>
/// Adapter that implements <see cref="ICredentialProtector"/> by delegating to
/// <see cref="IProfileCredentialProtector"/> with the profile resolved from the
/// ambient <see cref="IProfileContextAccessor"/>. Lets every existing call
/// site that asks for an <see cref="ICredentialProtector"/> remain unchanged
/// while still getting per-profile cryptographic isolation.
/// </summary>
/// <remarks>
/// Decorator pattern: this type does not perform any cryptography itself; it
/// simply forwards to the underlying protector with the current ambient
/// profile id.
/// </remarks>
public sealed class AmbientProfileCredentialProtector : ICredentialProtector
{
    private readonly IProfileCredentialProtector _inner;
    private readonly IProfileContextAccessor _accessor;

    /// <summary>
    /// Creates a new <see cref="AmbientProfileCredentialProtector"/>.
    /// </summary>
    public AmbientProfileCredentialProtector(IProfileCredentialProtector inner, IProfileContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(accessor);
        _inner = inner;
        _accessor = accessor;
    }

    /// <inheritdoc/>
    public (byte[] Cipher, byte[] Nonce) Encrypt(string plaintext)
        => _inner.Protect(_accessor.CurrentIdOrDefault, plaintext);

    /// <inheritdoc/>
    public string Decrypt(byte[] cipher, byte[] nonce)
        => _inner.Unprotect(_accessor.CurrentIdOrDefault, cipher, nonce);
}
