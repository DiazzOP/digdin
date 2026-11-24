namespace RbxSwitcher.Core.Accounts;

public enum UpsertResult { Added, Updated }

/// <summary>
/// In-memory list of accounts, keyed by Roblox user id (so the same account can never be
/// saved twice). Not thread-safe: the app touches it from the UI thread only.
/// </summary>
public sealed class AccountStore
{
    public const string NoGroup = "Sem grupo";
    private readonly List<Account> _accounts;

    public AccountStore(IEnumerable<Account>? accounts = null)
    {
        _accounts = new List<Account>();
        // Collapse duplicates that may come from an old or hand-edited vault: keep the newest session.
        foreach (var a in accounts ?? [])
        {
            var existing = Find(a.UserId);
            if (existing is null)
                _accounts.Add(a);
            else if ((a.LastValidUtc ?? a.AddedUtc) > (existing.LastValidUtc ?? existing.AddedUtc))
                _accounts[_accounts.IndexOf(existing)] = a;
        }
    }

    public IReadOnlyList<Account> All => _accounts;
    public int Count => _accounts.Count;

    public Account? Find(long userId) => _accounts.FirstOrDefault(a => a.UserId == userId);

    /// <summary>Adds a new account or, if this Roblox user is already saved, refreshes its session and names.</summary>
    public UpsertResult Upsert(long userId, string username, string displayName, string session)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        ArgumentException.ThrowIfNullOrEmpty(session);
        var now = DateTimeOffset.UtcNow;
        var existing = Find(userId);
        if (existing is not null)
        {
            existing.Username = username;
            existing.DisplayName = displayName;
            existing.Session = session;
            existing.Status = SessionStatus.Valid;
            existing.LastValidUtc = now;
            return UpsertResult.Updated;
        }
        _accounts.Add(new Account
        {
            UserId = userId,
            Username = username,
            DisplayName = displayName,
            Session = session,
            Status = SessionStatus.Valid,
            AddedUtc = now,
            LastValidUtc = now,
        });
        return UpsertResult.Added;
    }

    /// <summary>Replaces the session only if it is still <paramref name="expectedOld"/> (Roblox rotated it).</summary>
    public bool ReplaceSessionIf(long userId, string expectedOld, string fresh)
    {
        var a = Find(userId);
        if (a is null || a.Session != expectedOld || string.IsNullOrEmpty(fresh) || fresh == expectedOld)
            return false;
        a.Session = fresh;
        a.LastValidUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public void MarkExpired(long userId)
    {
        var a = Find(userId);
        if (a is null) return;
        a.Status = SessionStatus.Expired;
    }

    public bool Remove(long userId)
    {
        var a = Find(userId);
        if (a is null) return false;
        a.Session = "";
        return _accounts.Remove(a);
    }

    public IEnumerable<string> Groups =>
        _accounts.Select(a => a.GroupOrDefault).Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderBy(g => g == NoGroup ? 1 : 0).ThenBy(g => g, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Case-insensitive search over username, display name, alias and group; sorted by group then title.</summary>
    public IReadOnlyList<Account> Search(string? query, string? group = null)
    {
        IEnumerable<Account> q = _accounts;
        if (!string.IsNullOrWhiteSpace(group))
            q = q.Where(a => string.Equals(a.GroupOrDefault, group, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query))
        {
            string t = query.Trim().TrimStart('@');
            q = q.Where(a =>
                a.Username.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                a.DisplayName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (a.Alias?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Group?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                a.UserId.ToString() == t);
        }
        return q.OrderBy(a => a.GroupOrDefault == NoGroup ? 1 : 0)
                .ThenBy(a => a.GroupOrDefault, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }
}
