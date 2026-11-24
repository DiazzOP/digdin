using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RbxSwitcher.Core.Security;

/// <summary>The master password was wrong (or the file was altered).</summary>
public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("Senha mestra incorreta.") { }
}

/// <summary>The vault file is damaged, truncated or not a vault.</summary>
public sealed class VaultCorruptException : Exception
{
    public VaultCorruptException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Vault file format (all integers little-endian):
/// <code>
/// "RBXSW" (5) | version (1) = 1 | flags (1): bit0 = master password
/// [if password] iterations (4) | salt (16)
/// wrappedKeyLength (4) | wrappedKey (n)          -- IKeyProtector(inner)
///     inner = dataKey (32)                        without password
///     inner = nonce (12) | tag (16) | AES-GCM(pwKey, dataKey) (32)   with password
/// nonce (12) | tag (16) | ciphertext              -- AES-256-GCM(dataKey, payload), header as AAD
/// </code>
/// With a password both layers must open: the OS protector (DPAPI: this Windows account on
/// this PC) and the password-derived key (PBKDF2-HMAC-SHA256). A copy of the file alone, or
/// the Windows session alone, is not enough.
/// </summary>
public static class VaultCrypto
{
    private static readonly byte[] Magic = "RBXSW"u8.ToArray();
    private const byte Version = 1;
    private const byte FlagPassword = 1;
    public const int DefaultIterations = 600_000; // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    private const int MinIterations = 100_000;
    private const int KeySize = 32, NonceSize = 12, TagSize = 16, SaltSize = 16;

    public sealed record KeyMaterial(byte[] DataKey, PasswordParams? Password);

    public sealed record PasswordParams(int Iterations, byte[] Salt, byte[] PasswordKey);

    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static PasswordParams DerivePassword(string password, byte[]? salt = null, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        salt ??= RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        return new PasswordParams(iterations, salt, key);
    }

    public static bool HasPassword(byte[] file)
    {
        CheckMagic(file);
        return (file[Magic.Length + 1] & FlagPassword) != 0;
    }

    public static byte[] Seal(byte[] payload, KeyMaterial keys, IKeyProtector protector)
    {
        using var header = new MemoryStream();
        header.Write(Magic);
        header.WriteByte(Version);
        header.WriteByte(keys.Password is null ? (byte)0 : FlagPassword);

        byte[] inner;
        if (keys.Password is { } pw)
        {
            WriteInt(header, pw.Iterations);
            header.Write(pw.Salt);
            inner = AeadSeal(pw.PasswordKey, keys.DataKey, aad: Magic);
        }
        else
        {
            inner = (byte[])keys.DataKey.Clone();
        }

        byte[] wrapped;
        try { wrapped = protector.Protect(inner); }
        finally { CryptographicOperations.ZeroMemory(inner); }

        WriteInt(header, wrapped.Length);
        header.Write(wrapped);
        byte[] aad = header.ToArray();

        byte[] body = AeadSeal(keys.DataKey, payload, aad);
        byte[] file = new byte[aad.Length + body.Length];
        aad.CopyTo(file, 0);
        body.CopyTo(file, aad.Length);
        return file;
    }

    /// <param name="password">Required when the file has a master password; ignored otherwise.</param>
    public static (byte[] Payload, KeyMaterial Keys) Open(byte[] file, IKeyProtector protector, string? password)
    {
        CheckMagic(file);
        int pos = Magic.Length;
        if (file[pos++] != Version)
            throw new VaultCorruptException("Versão de cofre desconhecida.");
        bool hasPassword = (file[pos++] & FlagPassword) != 0;

        PasswordParams? pw = null;
        if (hasPassword)
        {
            int iterations = ReadInt(file, ref pos);
            if (iterations < MinIterations || iterations > 50_000_000)
                throw new VaultCorruptException("Parâmetros de senha inválidos.");
            byte[] salt = Slice(file, ref pos, SaltSize);
            if (string.IsNullOrEmpty(password))
                throw new WrongPasswordException();
            pw = DerivePassword(password, salt, iterations);
        }

        int wrappedLen = ReadInt(file, ref pos);
        if (wrappedLen <= 0 || wrappedLen > 64 * 1024)
            throw new VaultCorruptException("Chave do cofre inválida.");
        byte[] wrapped = Slice(file, ref pos, wrappedLen);
        byte[] aad = file.AsSpan(0, pos).ToArray();

        byte[] inner;
        try { inner = protector.Unprotect(wrapped); }
        catch (CryptographicException e)
        {
            throw new VaultCorruptException(
                "O Windows não conseguiu abrir a chave do cofre. Ele só abre na mesma conta do Windows em que foi criado.", e);
        }

        byte[] dataKey;
        if (pw is not null)
        {
            try { dataKey = AeadOpen(pw.PasswordKey, inner, aad: Magic); }
            catch (CryptographicException) { throw new WrongPasswordException(); }
            finally { CryptographicOperations.ZeroMemory(inner); }
        }
        else
        {
            dataKey = inner;
        }
        if (dataKey.Length != KeySize)
            throw new VaultCorruptException("Chave do cofre com tamanho inválido.");

        byte[] payload;
        try { payload = AeadOpen(dataKey, file.AsSpan(pos).ToArray(), aad); }
        catch (CryptographicException e) { throw new VaultCorruptException("O arquivo do cofre foi alterado ou está danificado.", e); }

        return (payload, new KeyMaterial(dataKey, pw));
    }

    private static byte[] AeadSeal(byte[] key, byte[] plaintext, byte[] aad)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] ct = new byte[plaintext.Length];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plaintext, ct, tag, aad);
        byte[] result = new byte[NonceSize + TagSize + ct.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ct.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    private static byte[] AeadOpen(byte[] key, byte[] sealedData, byte[] aad)
    {
        if (sealedData.Length < NonceSize + TagSize)
            throw new CryptographicException("truncated");
        var nonce = sealedData.AsSpan(0, NonceSize);
        var tag = sealedData.AsSpan(NonceSize, TagSize);
        var ct = sealedData.AsSpan(NonceSize + TagSize);
        byte[] pt = new byte[ct.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, ct, tag, pt, aad);
        return pt;
    }

    private static void CheckMagic(byte[] file)
    {
        if (file.Length < Magic.Length + 2 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new VaultCorruptException("Este arquivo não é um cofre do RbxSwitcher.");
    }

    private static void WriteInt(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static int ReadInt(byte[] file, ref int pos)
    {
        if (pos + 4 > file.Length) throw new VaultCorruptException("Cofre truncado.");
        int v = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos, 4));
        pos += 4;
        return v;
    }

    private static byte[] Slice(byte[] file, ref int pos, int len)
    {
        if (pos + len > file.Length) throw new VaultCorruptException("Cofre truncado.");
        byte[] r = file.AsSpan(pos, len).ToArray();
        pos += len;
        return r;
    }
}
