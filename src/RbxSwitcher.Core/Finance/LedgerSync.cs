using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Finance;

public sealed record LedgerSyncResult(TransferLedger? Ledger, LedgerCheckpoint? Checkpoint, bool Completed, string? Problem, string? RotatedSession, HistoryReadInfo Info);

/// <summary>
/// Keeps the 30-day history of outgoing transfers: one full read from the newest page, then incremental
/// reads (newest pages again until they reach the saved history), merged and deduplicated by the stable
/// transaction id.
/// <list type="bullet">
/// <item><b>Full read</b>: starts at the newest page with no cursor and no saved history. It is complete
/// when it reaches a record older than (start − 30 days) or Roblox says there is no next page.</item>
/// <item><b>Incremental read</b>: also starts at the newest page; complete when it reaches the time already
/// covered by the saved history. Result = saved history ∪ new records, deduplicated by id.</item>
/// <item>A paused read (error, 429, page budget) resumes only if it began at the newest page; the saved
/// complete history stays as it was. Partial history is never "zero sent".</item>
/// <item><b>Stable id</b>: the first of idHash/transactionId/id/purchaseToken present on every record and
/// unique on the page; then kept for the whole history. The same id with a different date or amount means
/// the field is not a transaction id: the read fails ("a conferir") instead of silently dropping records.</item>
/// <item>Order is checked within and across pages; a full page without a cursor or a repeated cursor fails.</item>
/// </list>
/// </summary>
public static class LedgerSync
{
    internal static readonly TimeSpan OverlapMargin = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan CheckpointMaxAge = TimeSpan.FromHours(24);
    internal static readonly TimeSpan KeepMargin = TimeSpan.FromDays(1);
    internal const int PageSize = 100;

    public delegate Task<ReadResult<TransferPage>> PageReader(PageCursor? cursor, CancellationToken ct);

    /// <param name="full">Ignore the saved history and checkpoint: read the full 30 days from the newest page.</param>
    public static async Task<LedgerSyncResult> SyncAsync(PageReader readPage, TransferLedger? ledger, LedgerCheckpoint? checkpoint,
                                                         Func<DateTimeOffset> clock, int maxPages, CancellationToken ct = default, bool full = false)
    {
        string? rotated = null;
        DateTimeOffset now = clock();
        if (full) { ledger = null; checkpoint = null; }
        if (ledger is not null && (ledger.Version != TransferLedger.CurrentVersion || ledger.IdField is null)) ledger = null;
        var cp = checkpoint is { StartedFromTop: true } && checkpoint.Version == TransferLedger.CurrentVersion
                 && now - checkpoint.AnchorUtc <= CheckpointMaxAge
                 && checkpoint.BaseCompleteAsOfUtc == ledger?.CompleteAsOfUtc
            ? checkpoint
            : Start(ledger, now);
        int pages = 0;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        // Every record seen in this run (incoming too), to tell exact repeats from id collisions.
        var seen = new Dictionary<string, (DateTimeOffset At, long Amount)>(StringComparer.Ordinal);
        void Seed()
        {
            seen.Clear();
            // The saved history too: an id already saved must come back with the same date and amount.
            if (ledger is not null && cp.BaseCompleteAsOfUtc == ledger.CompleteAsOfUtc)
                foreach (var e in ledger.Entries) seen[e.Id] = (e.CreatedUtc, -e.Amount);
            foreach (var e in cp.Entries) seen[e.Id] = (e.CreatedUtc, -e.Amount);
        }
        Seed();

        LedgerSyncResult Pause(string problem, string stop) => new(ledger, cp, false, problem, rotated, Info(cp, stop));
        // A broken id or order cannot be resumed: drop the checkpoint so the next read starts clean.
        LedgerSyncResult Fail(string problem, string stop) => new(ledger, null, false, problem, rotated, Info(cp, stop));

        while (true)
        {
            if (pages >= maxPages)
                return Pause("leitura do histórico pausada (continua na próxima atualização)", "pausada (limite de páginas)");

            PageCursor? cursor = cp.Cursor is null ? null : new PageCursor(cp.CursorParam, cp.Cursor);
            var page = await readPage(cursor, ct).ConfigureAwait(false);
            rotated = page.RotatedSession ?? rotated;
            if (!page.Ok)
                return Pause(page.Problem ?? "erro ao ler o histórico", "erro: " + (page.Problem ?? "leitura"));
            pages++;
            cp.PagesRead++;
            var items = page.Value!.Items;

            if (items.Count > 0 && cp.OldestUtc is { } prevOldest && items[0].CreatedUtc > prevOldest + EconomyParsers.OrderTolerance)
                return Fail("histórico fora de ordem entre páginas", "erro: ordem entre páginas");

            // Stable id: chosen once, verified on every page.
            if (items.Count > 0)
            {
                cp.IdField ??= ledger?.IdField ?? ChooseIdField(items);
                if (cp.IdField is null)
                    return Fail("nenhum campo de ID único nas transações", "erro: sem ID estável");
                if (items.Any(i => !i.Ids.ContainsKey(cp.IdField)))
                    return Fail($"campo de ID {cp.IdField} ausente em alguma transação", "erro: ID ausente");
                if (items.Select(i => i.Ids[cp.IdField]).Distinct(StringComparer.Ordinal).Count() != items.Count
                    && !items.GroupBy(i => i.Ids[cp.IdField]).All(g => g.Select(x => (x.CreatedUtc, x.Amount)).Distinct().Count() == 1))
                    return Fail($"campo {cp.IdField} repetido com dados diferentes: não identifica a transação", "erro: ID repetido");
            }

            var known = cp.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in items)
            {
                string id = item.Ids[cp.IdField!];
                cp.Records++;
                if (cp.NewestUtc is null || item.CreatedUtc > cp.NewestUtc) cp.NewestUtc = item.CreatedUtc;
                if (cp.OldestUtc is null || item.CreatedUtc < cp.OldestUtc) cp.OldestUtc = item.CreatedUtc;

                if (seen.TryGetValue(id, out var prev))
                {
                    if (prev.At != item.CreatedUtc || prev.Amount != item.Amount)
                        return Fail($"campo {cp.IdField} repetido com dados diferentes: não identifica a transação", "erro: ID repetido");
                    cp.Duplicates++;
                    continue; // the same transaction listed twice (page boundaries): counted once
                }
                seen[id] = (item.CreatedUtc, item.Amount);

                if (item.IsOutgoing)
                {
                    cp.OutgoingRaw++;
                    if (known.Add(id))
                        cp.Entries.Add(new TransferEntry { Id = id, CreatedUtc = item.CreatedUtc, Amount = Math.Abs(item.Amount) });
                }
                else cp.Incoming++;
            }

            DateTimeOffset? pageOldest = items.Count == 0 ? null : items.Min(i => i.CreatedUtc);
            var next = page.Value.NextCursor;
            bool incremental = ledger is not null && cp.BaseCompleteAsOfUtc == ledger.CompleteAsOfUtc && ledger.IdField == cp.IdField;
            // Proofs of completeness. All crawls here started at the newest page (StartedFromTop).
            bool pastWindow = pageOldest is { } o && o <= cp.AnchorUtc - QuotaCalculator.Window;
            bool reachedSaved = incremental && pageOldest is { } o2 && o2 <= ledger!.CompleteAsOfUtc - OverlapMargin;
            bool endOfHistory = next is null;

            if (endOfHistory && items.Count >= PageSize && !pastWindow && !reachedSaved)
                return Fail("página cheia sem indicação da próxima página", "erro: paginação não informada");

            if (endOfHistory || pastWindow || reachedSaved)
            {
                string stop = !incremental
                    ? (pastWindow ? "leitura completa: cobriu 30 dias" : "leitura completa: última página do Roblox")
                    : "leitura incremental: alcançou o histórico salvo";
                var info = Info(cp, stop);
                // Incremental: saved complete history + new records, deduplicated by id.
                ledger = Merge(cp, incremental ? ledger : null);
                now = clock();
                if (now - ledger.CompleteAsOfUtc > TimeSpan.FromMinutes(1))
                {
                    cp = Start(ledger, now); // a resumed crawl: catch up with what happened since it began
                    Seed();
                    continue;
                }
                return new LedgerSyncResult(ledger, null, true, null, rotated, info);
            }

            if (items.Count == 0)
                return Pause("histórico com página vazia antes do fim", "erro: página vazia");
            if (!seenCursors.Add(next!.Param + "=" + next.Value) || (next.Param == cp.CursorParam && next.Value == cp.Cursor))
                return Fail("paginação do histórico repetida", "erro: cursor repetido");
            cp.Cursor = next.Value;
            cp.CursorParam = next.Param;
        }
    }

    /// <summary>First id field present on every record and unique among them (identical repeats allowed).</summary>
    internal static string? ChooseIdField(IReadOnlyList<TransferRecord> items)
    {
        foreach (var f in EconomyParsers.IdFields)
        {
            if (!items.All(i => i.Ids.ContainsKey(f))) continue;
            bool unique = items.GroupBy(i => i.Ids[f]).All(g => g.Select(x => (x.CreatedUtc, x.Amount)).Distinct().Count() == 1);
            if (unique) return f;
        }
        return null;
    }

    private static HistoryReadInfo Info(LedgerCheckpoint cp, string stop) => new()
    {
        AtUtc = cp.AnchorUtc,
        Pages = cp.PagesRead,
        Records = cp.Records,
        Outgoing = cp.Entries.Count,
        OutgoingRaw = cp.OutgoingRaw,
        Duplicates = cp.Duplicates,
        Incoming = cp.Incoming,
        NewestUtc = cp.NewestUtc,
        OldestUtc = cp.OldestUtc,
        IdField = cp.IdField,
        Stop = stop,
        FullRead = cp.BaseCompleteAsOfUtc is null,
    };

    private static LedgerCheckpoint Start(TransferLedger? baseLedger, DateTimeOffset now) => new()
    {
        AnchorUtc = now,
        StartedFromTop = true,
        BaseCompleteAsOfUtc = baseLedger?.CompleteAsOfUtc,
        IdField = baseLedger?.IdField,
    };

    private static TransferLedger Merge(LedgerCheckpoint cp, TransferLedger? baseLedger)
    {
        var byId = new Dictionary<string, TransferEntry>(StringComparer.Ordinal);
        foreach (var e in baseLedger?.Entries ?? []) byId[e.Id] = e;
        foreach (var e in cp.Entries) byId[e.Id] = e;
        var keepFrom = cp.AnchorUtc - QuotaCalculator.Window - KeepMargin;
        return new TransferLedger
        {
            Version = TransferLedger.CurrentVersion,
            IdField = cp.IdField,
            CompleteAsOfUtc = cp.AnchorUtc,
            Entries = byId.Values.Where(e => e.CreatedUtc > keepFrom).OrderByDescending(e => e.CreatedUtc).ToList(),
        };
    }
}
