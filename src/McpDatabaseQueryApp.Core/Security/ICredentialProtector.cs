namespace McpDatabaseQueryApp.Core.Security;

public interface ICredentialProtector
{
    (byte[] Cipher, byte[] Nonce) Encrypt(string plaintext);

    string Decrypt(byte[] cipher, byte[] nonce);
}
