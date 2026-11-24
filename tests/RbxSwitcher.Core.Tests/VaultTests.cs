using System.Text;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Security;
using RbxSwitcher.Core.Storage;

namespace RbxSwitcher.Core.Tests;

public class VaultTests
{
    [Fact]
    public void Hundred_accounts_survive_close_and_reopen()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        var sessions = new Dictionary<long, string>();
        using (var v = VaultFile.Create(path, new FakeProtector()))
        {
            for (long id = 1; id <= 100; id++)
            {
                sessions[id] = Fake.Session(id.ToString());
                v.Accounts.Upsert(id, $"Account_{id:000}", $"Display {id}", sessions[id]);
            }
            v.Accounts.Find(35)!.Group = "Trades";
            v.Accounts.Find(35)!.Alias = "Principal";
            v.Save();
        }

        using var reopened = VaultFile.Open(path, new FakeProtector(), null);
        Assert.Equal(100, reopened.Accounts.Count);
        foreach (var (id, s) in sessions)
            Assert.Equal(s, reopened.Accounts.Find(id)!.RevealSession());
        Assert.Equal("Trades", reopened.Accounts.Find(35)!.Group);
        Assert.Equal("Principal", reopened.Accounts.Find(35)!.Title);
    }

    [Fact]
    public void The_cheapest_listings_survive_close_and_reopen()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        var read = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        using (var v = VaultFile.Create(path, new FakeProtector()))
        {
            v.Resellers[7] = new Limiteds.ItemListings(7, [new(12, 3_050, 901, null, 4), new(99, 3_100, null, "x-1", null)], read, Complete: true);
            v.Save();
        }
        using var reopened = VaultFile.Open(path, new FakeProtector(), null);
        var l = reopened.Resellers[7];
        Assert.Equal((read, true, 2), (l.ReadUtc, l.Complete, l.Lowest.Count));
        Assert.Equal((12L, 3_050L, (long?)901, (int?)4), (l.Lowest[0].SellerId, l.Lowest[0].Price, l.Lowest[0].UserAssetId, l.Lowest[0].Serial));
    }

    [Fact]
    public void File_on_disk_contains_no_plaintext_session_or_username()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        string session = Fake.Session();
        using (var v = VaultFile.Create(path, new FakeProtector()))
        {
            v.Accounts.Upsert(42, "VisibleUserName", "Shown", session);
            v.Save();
        }
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("WARNING", raw);
        Assert.DoesNotContain("VisibleUserName", raw);
        Assert.DoesNotContain(session[^20..], raw);
    }

    [Fact]
    public void Another_windows_user_cannot_open_the_vault()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        using (VaultFile.Create(path, new FakeProtector("alice"))) { }
        var e = Assert.Throws<VaultCorruptException>(() => VaultFile.Open(path, new FakeProtector("mallory"), null));
        Assert.Contains("Windows", e.Message);
    }

    [Fact]
    public void Master_password_is_required_and_checked()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        using (var v = VaultFile.Create(path, new FakeProtector(), "correct horse"))
        {
            v.Accounts.Upsert(1, "a", "a", Fake.Session());
            v.Save();
        }
        Assert.True(VaultFile.NeedsPassword(path));
        Assert.Throws<WrongPasswordException>(() => VaultFile.Open(path, new FakeProtector(), null));
        Assert.Throws<WrongPasswordException>(() => VaultFile.Open(path, new FakeProtector(), "wrong"));
        using var ok = VaultFile.Open(path, new FakeProtector(), "correct horse");
        Assert.Equal(1, ok.Accounts.Count);
    }

    [Fact]
    public void Password_with_right_password_but_other_windows_user_still_fails()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        using (VaultFile.Create(path, new FakeProtector("alice"), "pw-123456")) { }
        Assert.Throws<VaultCorruptException>(() => VaultFile.Open(path, new FakeProtector("mallory"), "pw-123456"));
    }

    [Fact]
    public void Changing_and_removing_password_works_and_old_password_stops_working()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        using (var v = VaultFile.Create(path, new FakeProtector(), "old-password"))
        {
            v.Accounts.Upsert(7, "u", "d", Fake.Session());
            v.SetPassword("new-password");
            Assert.True(v.CheckPassword("new-password"));
            Assert.False(v.CheckPassword("old-password"));
        }
        Assert.Throws<WrongPasswordException>(() => VaultFile.Open(path, new FakeProtector(), "old-password"));
        // the .bak must not keep the old password alive either
        Assert.Throws<WrongPasswordException>(() => VaultFile.Open(path + ".bak", new FakeProtector(), "old-password"));
        using (var v = VaultFile.Open(path, new FakeProtector(), "new-password"))
            v.SetPassword(null);
        Assert.False(VaultFile.NeedsPassword(path));
        using var open = VaultFile.Open(path, new FakeProtector(), null);
        Assert.NotNull(open.Accounts.Find(7));
    }

    [Fact]
    public void Tampered_file_is_rejected_and_backup_is_used()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        using (var v = VaultFile.Create(path, new FakeProtector()))
        {
            v.Accounts.Upsert(1, "first", "f", Fake.Session());
            v.Save();
            v.Save(); // .bak now holds a copy that already has the account
        }
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^5] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<VaultCorruptException>(() => VaultCrypto.Open(bytes, new FakeProtector(), null));

        using var recovered = VaultFile.Open(path, new FakeProtector(), null);
        Assert.Equal("first", recovered.Accounts.Find(1)!.Username);
    }

    [Fact]
    public void Finance_and_history_checkpoint_persist_encrypted()
    {
        using var dir = new TempDir();
        string path = dir.File("vault.bin");
        var at = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        using (var v = VaultFile.Create(path, new FakeProtector()))
        {
            v.Accounts.Upsert(9, "u", "u", Fake.Session());
            v.Accounts.Find(9)!.Finance = new AccountFinance
            {
                Robux = 1_693, RobuxAtUtc = at,
                Caps = new TransferCaps { Daily = 5_000, Monthly = 10_000, PerTransfer = 2_000 }, CapsAtUtc = at,
                Ledger = new TransferLedger { Version = TransferLedger.CurrentVersion, CompleteAsOfUtc = at, Entries = [new TransferEntry { Id = "TXN-SECRET-ID-123", CreatedUtc = at, Amount = 250 }] },
                Checkpoint = new LedgerCheckpoint { AnchorUtc = at, Cursor = "CURSOR-XYZ" },
            };
            v.Save();
        }
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("TXN-SECRET-ID-123", raw);
        Assert.DoesNotContain("CURSOR-XYZ", raw);

        using var reopened = VaultFile.Open(path, new FakeProtector(), null);
        var f = reopened.Accounts.Find(9)!.Finance!;
        Assert.Equal(1_693, f.Robux);
        Assert.Equal(at, f.RobuxAtUtc);
        Assert.Equal(2_000, f.Caps!.PerTransfer);
        Assert.Equal("TXN-SECRET-ID-123", f.Ledger!.Entries.Single().Id);
        Assert.Equal("CURSOR-XYZ", f.Checkpoint!.Cursor);
    }

    [Fact]
    public void Not_a_vault_is_rejected()
    {
        Assert.Throws<VaultCorruptException>(() => VaultCrypto.Open(Encoding.UTF8.GetBytes("hello world"), new FakeProtector(), null));
    }
}
