using System.Security.Cryptography;
using System.Text.Json;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Security;

namespace RbxSwitcher.Core.Storage;

public sealed class AppSettings
{
    /// <summary>Lock after this many minutes without keyboard/mouse input. 0 = never.</summary>
    public int AutoLockMinutes { get; set; } = 15;

    /// <summary>How many account browsers stay alive for instant switching (each costs memory).</summary>
    public int LiveSessions { get; set; } = 5;

    /// <summary>Re-check every saved session this often while the app is open (minutes).</summary>
    public int RecheckMinutes { get; set; } = 30;

    /// <summary>Daily / 30-day / per-transfer details under the balance summary (collapsed by default).</summary>
    public bool FinanceDetailsExpanded { get; set; }

    /// <summary>GitHub "owner/repo" whose releases update the app. Null = updates off (the default).</summary>
    public string? UpdateRepo { get; set; }

    /// <summary>Limited distribution: fee, net ceiling, Small limit, sources and destinations.</summary>
    public Limiteds.DistributionSettings Distribution { get; set; } = new();
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
}

internal sealed class VaultPayload
{
    public int Schema { get; set; } = 1;
    public List<Account> Accounts { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
    /// <summary>Avatars saved in the builder (names and catalog item ids only).</summary>
    public List<Avatars.AvatarPreset> Avatars { get; set; } = [];
    /// <summary>The Limited distribution plan (steps, history, exclusions).</summary>
    public Limiteds.DistributionPlan? Plan { get; set; }
    /// <summary>The cheapest listings of each item (they hold the accounts' own listings, so they stay in the vault).</summary>
    public Dictionary<long, Limiteds.ItemListings>? Resellers { get; set; }
}

/// <summary>
/// The encrypted file holding accounts, sessions and settings. Writes are atomic
/// (temp file + replace) and keep one previous copy as <c>.bak</c>.
/// </summary>
public sealed class VaultFile : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IKeyProtector _protector;
    private VaultCrypto.KeyMaterial _keys;
    private readonly object _writeLock = new();

    public AccountStore Accounts { get; }
    public AppSettings Settings { get; }
    public List<Avatars.AvatarPreset> Avatars { get; }
    public Limiteds.DistributionPlan Plan { get; set; }
    /// <summary>The cheapest listings of each item, by asset id (Limiteds tab).</summary>
    public Dictionary<long, Limiteds.ItemListings> Resellers { get; set; }
    public bool HasPassword => _keys.Password is not null;
    public string Path => _path;

    private VaultFile(string path, IKeyProtector protector, VaultCrypto.KeyMaterial keys, VaultPayload payload)
    {
        _path = path;
        _protector = protector;
        _keys = keys;
        Accounts = new AccountStore(payload.Accounts);
        Settings = payload.Settings ?? new AppSettings();
        Avatars = payload.Avatars ?? [];
        Plan = payload.Plan ?? new Limiteds.DistributionPlan();
        Resellers = payload.Resellers ?? [];
    }

    public static bool Exists(string path) => File.Exists(path) || File.Exists(path + ".bak");

    /// <summary>True if opening needs the master password.</summary>
    public static bool NeedsPassword(string path)
    {
        string p = File.Exists(path) ? path : path + ".bak";
        return VaultCrypto.HasPassword(File.ReadAllBytes(p));
    }

    public static VaultFile Create(string path, IKeyProtector protector, string? password = null)
    {
        if (File.Exists(path))
            throw new IOException("Já existe um cofre neste local.");
        var keys = new VaultCrypto.KeyMaterial(VaultCrypto.NewDataKey(),
            string.IsNullOrEmpty(password) ? null : VaultCrypto.DerivePassword(password));
        var vault = new VaultFile(path, protector, keys, new VaultPayload());
        vault.Save();
        return vault;
    }

    /// <summary>Opens the vault; falls back to the <c>.bak</c> copy if the main file is damaged.</summary>
    public static VaultFile Open(string path, IKeyProtector protector, string? password)
    {
        Exception? first = null;
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var (payloadBytes, keys) = VaultCrypto.Open(File.ReadAllBytes(candidate), protector, password);
                VaultPayload payload;
                try { payload = JsonSerializer.Deserialize<VaultPayload>(payloadBytes, Json) ?? new VaultPayload(); }
                finally { CryptographicOperations.ZeroMemory(payloadBytes); }
                var vault = new VaultFile(path, protector, keys, payload);
                if (candidate != path) vault.Save(); // restore the main file from the backup
                return vault;
            }
            catch (WrongPasswordException) { throw; }
            catch (Exception e) when (e is VaultCorruptException or JsonException or IOException)
            {
                first ??= e;
            }
        }
        throw first ?? new FileNotFoundException("Cofre não encontrado.", path);
    }

    public void Save()
    {
        var payload = new VaultPayload { Accounts = Accounts.All.ToList(), Settings = Settings, Avatars = Avatars, Plan = Plan, Resellers = Resellers };
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        byte[] sealedBytes;
        try { sealedBytes = VaultCrypto.Seal(plain, _keys, _protector); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        lock (_writeLock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!);
            string tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(sealedBytes);
                fs.Flush(true);
            }
            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, _path);
        }
    }

    /// <summary>Sets, changes (<paramref name="newPassword"/> non-empty) or removes (null/empty) the master password.</summary>
    public void SetPassword(string? newPassword)
    {
        var old = _keys;
        _keys = new VaultCrypto.KeyMaterial(old.DataKey,
            string.IsNullOrEmpty(newPassword) ? null : VaultCrypto.DerivePassword(newPassword));
        Save();
        // The .bak still holds the old wrapping; overwrite it so the old password stops working.
        try { File.Copy(_path, _path + ".bak", overwrite: true); } catch (IOException) { }
        if (old.Password is { } p) CryptographicOperations.ZeroMemory(p.PasswordKey);
    }

    /// <summary>Checks a password against the one in use (for "confirm current password" prompts).</summary>
    public bool CheckPassword(string password)
    {
        if (_keys.Password is not { } p) return true;
        var probe = VaultCrypto.DerivePassword(password, p.Salt, p.Iterations);
        bool ok = CryptographicOperations.FixedTimeEquals(probe.PasswordKey, p.PasswordKey);
        CryptographicOperations.ZeroMemory(probe.PasswordKey);
        return ok;
    }

    /// <summary>Best effort: .NET strings (the sessions) cannot be wiped, but keys can.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_keys.DataKey);
        if (_keys.Password is { } p) CryptographicOperations.ZeroMemory(p.PasswordKey);
    }
}
