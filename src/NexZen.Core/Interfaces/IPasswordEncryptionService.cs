namespace NexZen.Core.Interfaces;

public interface IPasswordEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}
