using System.Security.Cryptography;
using RbxSwitcher.Core.Security;

namespace RbxSwitcher.Core.Tests;

/// <summary>Stands in for DPAPI: a fixed per-"user" key, so a blob from another "user" fails like DPAPI would.</summary>
internal sealed class FakeProtector(string user = "alice") : IKeyProtector
{
    private readonly byte[] _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("user:" + user));

    public byte[] Protect(byte[] plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], ct = new byte[plaintext.Length];
        using var g = new AesGcm(_key, 16);
        g.Encrypt(nonce, plaintext, ct, tag);
        return [.. nonce, .. tag, .. ct];
    }

    public byte[] Unprotect(byte[] blob)
    {
        byte[] pt = new byte[blob.Length - 28];
        using var g = new AesGcm(_key, 16);
        g.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), pt); // throws CryptographicException
        return pt;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("rbxsw-test-").FullName;
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

internal static class Fake
{
    /// <summary>Something shaped like a real .ROBLOSECURITY (never a real one).</summary>
    public static string Session(string tag = "a") =>
        "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_"
        + tag + new string('X', 200) + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
}
