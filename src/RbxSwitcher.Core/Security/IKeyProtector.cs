namespace RbxSwitcher.Core.Security;

/// <summary>
/// Wraps the vault's data key with a secret held by the operating system.
/// On Windows this is DPAPI (CurrentUser scope): the wrapped key opens only for the
/// same Windows account on the same machine. Tests use a fake.
/// </summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] plaintext);

    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The blob was made by another user/machine or was tampered with.
    /// </exception>
    byte[] Unprotect(byte[] blob);
}
