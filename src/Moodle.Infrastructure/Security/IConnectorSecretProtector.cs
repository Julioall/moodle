namespace Moodle.Infrastructure;

internal interface IConnectorSecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}