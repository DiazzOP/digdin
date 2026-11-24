using System.Security.Cryptography;
using System.Text;
using RbxSwitcher.Core.Security;

namespace RbxSwitcher.App.Platform;

/// <summary>
/// DPAPI, CurrentUser scope: the blob opens only for this Windows account on this PC.
/// The extra entropy ties it to this app, so another app calling DPAPI on a copied blob
/// without knowing it gets nothing. (Malware already running as you can still call DPAPI
/// with the same entropy: that is what the optional master password is for.)
/// </summary>
internal sealed class DpapiProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RbxSwitcher.vault-key.v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
}
