using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>What the planner needs to know about one account (no session).</summary>
public sealed record AccountSnapshot(long UserId, string Username, string? Group, bool SessionOk, AccountFinance? Finance, AccountInventory? Inventory);

public sealed record PlanInput(IReadOnlyList<AccountSnapshot> Accounts, DistributionSettings Settings,
                               IReadOnlyDictionary<long, ItemMarket> Market, DateTimeOffset Now);

/// <summary>
/// Suggests which Limited to send, from which source, to which destination and with which Smalls — and why. A greedy,
/// explainable heuristic (like the Limited Desk's): copies released first, then liquidity (≥ 1 sale/day ties), then net;
/// destinations by "fits an already planned trade", then most free space. Only <see cref="StepState.Suggested"/> steps
/// are rebuilt; steps the operator acted on are kept and their copies stay reserved.
/// </summary>
public static class Planner
{
    public static DistributionRole RoleOf(DistributionSettings s, long userId, string? group)
    {
        if (s.SourceAccounts.Count == 0) return DistributionRole.None;
        if (s.SourceAccounts.Contains(userId)) return DistributionRole.Source;
        // Chosen one by one: exactly those (the group is only how the list was started).
        if (s.DestinationAccounts.Count > 0) return s.DestinationAccounts.Contains(userId) ? DistributionRole.Destination : DistributionRole.None;
        return s.DestinationGroup is not { } g || string.Equals(group, g, StringComparison.Ordinal) ? DistributionRole.Destination : DistributionRole.None;
    }

    /// <summary>Copies held by steps the operator is working on (opened, sent, waiting for a check).</summary>
    public static HashSet<long> Reserved(DistributionPlan plan) =>
        plan.Steps.Where(x => x.State is StepState.Opened or StepState.Sent or StepState.NeedsCheck)
            .SelectMany(x => x.AllCopies()).Select(c => c.UserAssetId).ToHashSet();

    /// <summary>Net already on its way to a destination (entradas comprometidas).</summary>
    public static long Committed(DistributionPlan plan, long destinationId, string? exceptStep = null) =>
        plan.Steps.Where(x => x.DestinationId == destinationId && x.Id != exceptStep && x.State is StepState.Opened or StepState.Sent or StepState.NeedsCheck)
            .Sum(x => x.Net);

    public static long? Space(AccountSnapshot a, DistributionSettings s, IReadOnlyDictionary<long, ItemMarket> market, DateTimeOffset now, long committed, out SpaceView view)
    {
        view = Valuation.Space(a.UserId, a.Finance, a.Inventory, s, market, now, committed);
        return view.Blocked ? null : s.UseSpaceAtSale ? view.SpaceAtSale : view.SpaceNow;
    }

    /// <summary>(score, text): sales per day capped at 1 (≥ 1/day tie), or Rolimons demand as a weaker stand-in.</summary>
    public static (double Score, string Text) Liquidity(ItemMarket? m) => m switch
    {
        { SalesPerDay: { } d } => (Math.Min(d, 1), $"{d.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))} {(d is >= 1 and < 2 ? "venda" : "vendas")}/dia"),
        { Demand: { } dm } => (dm switch { >= 4 => 1, 3 => .7, 2 => .4, 1 => .2, _ => .05 }, $"demanda {dm}/4 na Rolimons"),
        _ => (0, "liquidez desconhecida"),
    };

    public static DistributionPlan Build(PlanInput input, DistributionPlan current)
    {
        var s = input.Settings;
        var now = input.Now;
        var plan = new DistributionPlan
        {
            // A suggestion the operator discarded before opening it is not history.
            Steps = current.Steps.Where(x => x.State != StepState.Suggested && !(x.State == StepState.Discarded && x.OpenedUtc is null && x.TradeId is null)).ToList(),
            NextNumber = current.NextNumber,
            BuiltUtc = now,
            // A discarded copy stays out until brought back; one that left its source (sold, traded) is forgotten.
            Dismissed = current.Dismissed.Where(d => input.Accounts.Any(a => RoleOf(s, a.UserId, a.Group) == DistributionRole.Source
                && (a.Inventory is not { Complete: true } || a.Inventory.Copies.Any(c => c.UserAssetId == d.UserAssetId)))).ToList(),
        };
        var dismissed = plan.Dismissed.GroupBy(d => d.UserAssetId).ToDictionary(g => g.Key, g => g.ToList());
        // Not to the destination it was discarded for (an entry without one: to none).
        bool Barred(long uaid, long destId) => dismissed.TryGetValue(uaid, out var ds) && ds.Any(d => d.DestinationId is null || d.DestinationId == destId);
        var oldSuggested = current.Steps.Where(x => x.State == StepState.Suggested).ToList();
        var reserved = Reserved(plan);
        var exclusions = new List<Exclusion>();

        var accounts = input.Accounts.Select(a => (A: a, Role: RoleOf(s, a.UserId, a.Group))).ToList();

        // ── Destinations: space left after what is already on its way ──
        var dests = new List<Dest>();
        foreach (var (a, role) in accounts)
        {
            if (role != DistributionRole.Destination) continue;
            var d = new Dest(a);
            if (!a.SessionOk) d.Blocked = "sessão expirada";
            else if (Space(a, s, input.Market, now, Committed(plan, a.UserId), out var view) is { } space) { d.Remaining = d.Before = space; d.View = view; }
            else d.Blocked = string.Join("; ", view.Blocks);
            if (a.Inventory is { } inv)
                d.Smalls = Valuation.Value(inv, s, input.Market).Where(v => v.Small && !reserved.Contains(v.Copy.UserAssetId))
                    .OrderBy(v => v.OnHold).ThenBy(v => v.Copy.Rap ?? 0).ToList();
            dests.Add(d);
        }

        // ── Sources: their free copies, in the order the Desk uses ──
        var candidates = new List<(AccountSnapshot Src, ValuedCopy V, ItemMarket? M)>();
        var sourceSmalls = new Dictionary<long, List<ValuedCopy>>();
        var sourceSmallTotal = new Dictionary<long, int>();
        foreach (var (a, role) in accounts)
        {
            if (role != DistributionRole.Source || a.Inventory is not { } inv) continue;
            var valued = Valuation.Value(inv, s, input.Market);
            var smalls = valued.Where(v => v.Small && !reserved.Contains(v.Copy.UserAssetId)).ToList();
            // Only released Smalls count for the reserve: one on hold cannot go in an AstraFlip withdrawal yet.
            sourceSmalls[a.UserId] = smalls.Where(v => !v.OnHold).OrderBy(v => v.Copy.Rap ?? 0).ToList();
            sourceSmallTotal[a.UserId] = sourceSmalls[a.UserId].Count;
            string? sourceBlock = !a.SessionOk ? "a sessão da conta de origem expirou"
                : !inv.Complete ? "o inventário da origem não foi lido por completo"
                : PlusStatus.Evaluate(a.Finance, now).Label is not (PlusLabel.Active or PlusLabel.Trial) ? "a conta de origem está sem Plus ativo confirmado (trades exigem Plus)"
                : null;
            foreach (var v in valued.Where(v => !v.Small))
            {
                if (reserved.Contains(v.Copy.UserAssetId)) continue; // already in a step being worked on
                var cref = CopyRef.From(v);
                if (sourceBlock is not null) exclusions.Add(new(cref, a.UserId, a.Username, sourceBlock));
                else if (v.Net is null) exclusions.Add(new(cref, a.UserId, a.Username, "sem preço estimado (defina um preço manual ou aguarde o RAP)"));
                else candidates.Add((a, v, input.Market.GetValueOrDefault(v.Copy.AssetId)));
            }
        }
        // Released copies first (they can trade now); copies on hold are planned after them, as steps that wait for the release.
        candidates = candidates.OrderBy(c => c.V.OnHold).ThenByDescending(c => Liquidity(c.M).Score).ThenByDescending(c => c.V.Net).ThenBy(c => c.V.Copy.UserAssetId).ToList();

        var building = new List<PlanStep>();
        var usedSourceSmalls = new Dictionary<long, int>();
        foreach (var (src, v, m) in candidates)
        {
            long net = v.Net!.Value;
            var cref = CopyRef.From(v);
            var open = dests.Where(d => d.Blocked is null).ToList();
            var fits = open.Where(d => d.Remaining >= net && !Barred(v.Copy.UserAssetId, d.A.UserId)).ToList();
            if (fits.Count == 0 && dismissed.TryGetValue(v.Copy.UserAssetId, out var off))
            {
                var last = off.MaxBy(d => d.AtUtc)!;
                string where = off.Any(d => d.DestinationId is null) ? "" : " para " + string.Join(", ", off.Select(d => "@" + d.DestinationName).Distinct());
                exclusions.Add(new(cref, src.UserId, src.Username, HeldNote(v) +
                    $"sugestão descartada por você{where} em {BrasiliaTime.ToBrasilia(last.AtUtc):dd/MM HH:mm}; nenhuma outra destinatária com espaço", Dismissed: true));
                continue;
            }
            if (fits.Count == 0)
            {
                exclusions.Add(new(cref, src.UserId, src.Username, HeldNote(v) + (open.Count == 0
                    ? "nenhuma destinatária liberada: " + Brief(dests.Select(d => $"@{d.A.Username} ({d.Blocked})"))
                    : $"sem espaço para {Fmt(net)} líquidos: o maior espaço livre é {Fmt(open.Max(d => d.Remaining))} em @{open.MaxBy(d => d.Remaining)!.A.Username}")));
                continue;
            }
            // 1. Fit into a trade already planned in this pass (same source and destination, < 3 Limiteds).
            // A copy on hold only joins a trade that waits too (it would hold up a trade that could go now).
            var join = building.Where(x => x.SourceId == src.UserId && x.Limiteds.Count < PlanStep.MaxLimiteds && x.WaitsForRelease == v.OnHold)
                .Select(x => (Step: x, D: fits.FirstOrDefault(d => d.A.UserId == x.DestinationId)))
                .Where(t => t.D is not null).OrderByDescending(t => t.D!.Remaining).FirstOrDefault();
            if (join.Step is not null)
            {
                var d = join.D!;
                join.Step.Limiteds.Add(cref);
                join.Step.Why.Add($"{cref.Label}: encaixada na trade já planejada para @{d.A.Username} (até {PlanStep.MaxLimiteds} Limiteds por trade) · {Liquidity(m).Text}.");
                d.Remaining -= net;
                continue;
            }
            // 2. A new trade with the destination that has the most free space and a Small to send.
            var withSmall = fits.Where(d => d.Smalls.Count > 0).OrderByDescending(d => d.Remaining).ThenByDescending(d => d.A.Finance?.Caps?.Daily ?? 0).ToList();
            if (withSmall.Count == 0)
            {
                exclusions.Add(new(cref, src.UserId, src.Username,
                    HeldNote(v) + "as destinatárias com espaço não têm Small para enviar na troca: " + Brief(fits.Select(d => "@" + d.A.Username))));
                continue;
            }
            var dest = withSmall[0];
            var destSmall = dest.Smalls[0];
            dest.Smalls.RemoveAt(0);
            var step = new PlanStep
            {
                SourceId = src.UserId, SourceName = src.Username, DestinationId = dest.A.UserId, DestinationName = dest.A.Username,
                Limiteds = [cref], DestinationSmall = CopyRef.From(destSmall), WaitsForSmall = destSmall.OnHold, WaitsForRelease = v.OnHold, CreatedUtc = now,
            };
            step.Why.Add($"Destino @{dest.A.Username}: o maior espaço livre entre {P(fits.Count, "destinatária", "destinatárias")} com espaço ({Fmt(dest.Remaining)} {(s.UseSpaceAtSale ? $"na venda, em {P(s.DaysToSale, "dia", "dias")}" : "agora")}).");
            if (dest.View is { } dv && building.All(b => b.DestinationId != dest.A.UserId)) step.Why.AddRange(SpaceSum(dest.A.Username, dv, s));
            step.Why.Add(v.OnHold
                ? $"{cref.Label}: ainda em holding na origem — a etapa fica pronta e espera a liberação · {Liquidity(m).Text} · líquido {Fmt(net)} ({SourceText(v.Source)})."
                : $"{cref.Label}: liberada na origem · {Liquidity(m).Text} · líquido {Fmt(net)} ({SourceText(v.Source)}).");
            // The source's Small: only beyond the reserve kept for AstraFlip withdrawals.
            int used = usedSourceSmalls.GetValueOrDefault(src.UserId);
            var srcSmalls = sourceSmalls.GetValueOrDefault(src.UserId) ?? [];
            if (sourceSmallTotal.GetValueOrDefault(src.UserId) - used > s.MinSmallsReserve)
            {
                step.SourceSmall = CopyRef.From(srcSmalls[used]);
                // It arrives at the destination with this trade (on hold): a later trade there can wait for it.
                dest.Smalls.Add(srcSmalls[used] with { OnHold = true });
                dest.Incoming.Add(srcSmalls[used].Copy.UserAssetId);
                usedSourceSmalls[src.UserId] = used + 1;
                step.Why.Add($"Small da origem: {step.SourceSmall.Label} (a origem guarda {P(s.MinSmallsReserve, "Small", "Smalls")} para os saques da AstraFlip).");
            }
            else step.Why.Add($"Sem Small da origem: ela só tem a reserva de {P(s.MinSmallsReserve, "Small liberado", "Smalls liberados")} para os saques da AstraFlip.");
            step.Why.Add(dest.Incoming.Contains(destSmall.Copy.UserAssetId)
                ? $"Small de @{dest.A.Username}: {destSmall.Copy.Name}, que chega com outra trade deste plano — a etapa espera ele sair do holding."
                : destSmall.OnHold
                ? $"Small de @{dest.A.Username}: {destSmall.Copy.Name} ainda está em holding — a etapa espera a liberação."
                : $"Small de @{dest.A.Username}: {destSmall.Copy.Name}.");
            dest.Remaining -= net;
            building.Add(step);
        }

        foreach (var step in building)
        {
            var d = dests.First(x => x.A.UserId == step.DestinationId);
            step.Why.Add($"Espaço de @{d.A.Username}: {Fmt(d.Before)} → {Fmt(d.Before - building.Where(b => b.DestinationId == d.A.UserId).Sum(b => b.Net))} depois do plano.");
            // Same composition as an earlier suggestion: keep its id and date (the operator recognises it).
            var same = oldSuggested.FirstOrDefault(o => o.SourceId == step.SourceId && o.DestinationId == step.DestinationId &&
                o.Limiteds.Select(c => c.UserAssetId).Order().SequenceEqual(step.Limiteds.Select(c => c.UserAssetId).Order()));
            step.Id = same?.Id ?? plan.NewId();
            step.CreatedUtc = same?.CreatedUtc ?? now;
            if (same is null) step.Log(now, "sugerida");
            else step.History = same.History;
            plan.Steps.Add(step);
        }
        plan.Exclusions = exclusions;
        return plan;
    }

    /// <summary>
    /// Re-checks a step right before "Abrir trade": fresh reads, Plus on both sides, copies still where they should be and
    /// free, and space at the destination. Empty = "Pronta para trade".
    /// </summary>
    public static List<string> ReadyCheck(PlanStep step, PlanInput input, DistributionPlan plan)
    {
        var problems = new List<string>();
        var src = input.Accounts.FirstOrDefault(a => a.UserId == step.SourceId);
        var dst = input.Accounts.FirstOrDefault(a => a.UserId == step.DestinationId);
        if (src is null || dst is null) { problems.Add("conta da etapa não encontrada"); return problems; }
        foreach (var a in new[] { src, dst })
        {
            if (!a.SessionOk) problems.Add($"@{a.Username}: sessão expirada");
            if (a.Inventory?.ReadUtc is not { } ir || input.Now - ir > QuotaCalculator.MaxAge) problems.Add($"@{a.Username}: Limiteds lidos há mais de 30 minutos");
            if (PlusStatus.Evaluate(a.Finance, input.Now).Label is not (PlusLabel.Active or PlusLabel.Trial)) problems.Add($"@{a.Username}: sem Plus ativo confirmado");
        }
        // The destination's space comes from its balance and pending Robux: an old read can hide a sale that filled it.
        if (dst.Finance?.RobuxAtUtc is not { } fr || input.Now - fr > QuotaCalculator.MaxAge) problems.Add($"@{dst.Username}: saldo lido há mais de 30 minutos");
        var srcCopies = src.Inventory?.Copies.ToDictionary(c => c.UserAssetId) ?? [];
        var dstCopies = dst.Inventory?.Copies.ToDictionary(c => c.UserAssetId) ?? [];
        foreach (var c in step.Limiteds.Concat(step.SourceSmall is { } ss ? [ss] : []))
        {
            if (!srcCopies.TryGetValue(c.UserAssetId, out var now)) problems.Add($"{c.Label} não está mais em @{src.Username}");
            else if (src.Inventory!.IsOnHold(now)) problems.Add($"{c.Label} está em holding");
        }
        if (step.DestinationSmall is { } ds)
        {
            if (!dstCopies.TryGetValue(ds.UserAssetId, out var dc))
                problems.Add(step.WaitsForSmall ? $"o Small {ds.Label} ainda vai chegar em @{dst.Username} (outra trade do plano)" : $"o Small {ds.Label} não está mais em @{dst.Username}");
            else if (dst.Inventory!.IsOnHold(dc)) problems.Add($"o Small {ds.Label} ainda está em holding");
        }
        if (Space(dst, input.Settings, input.Market, input.Now, Committed(plan, dst.UserId, step.Id), out var view) is not { } space)
            problems.Add($"@{dst.Username} sem espaço calculável: " + string.Join("; ", view.Blocks));
        else if (space < step.Net) problems.Add($"@{dst.Username} tem {Fmt(space)} de espaço e a etapa precisa de {Fmt(step.Net)}");
        return problems;
    }

    /// <summary>
    /// After new readings: steps whose copies moved are flagged. Copies already at the destination = probably traded
    /// outside the app ("localize a trade"); an opened step whose composition no longer holds asks for a new check.
    /// </summary>
    public static void Revalidate(DistributionPlan plan, PlanInput input)
    {
        foreach (var step in plan.Steps.Where(x => x.State is StepState.Suggested or StepState.Opened))
        {
            var src = input.Accounts.FirstOrDefault(a => a.UserId == step.SourceId);
            var dst = input.Accounts.FirstOrDefault(a => a.UserId == step.DestinationId);
            if (src?.Inventory is not { Complete: true, ReadUtc: { } sr } || sr < step.CreatedUtc) continue;
            var missing = step.Limiteds.Where(c => src.Inventory.Copies.All(x => x.UserAssetId != c.UserAssetId)).ToList();
            if (missing.Count == 0) continue;
            bool atDestination = dst?.Inventory is { } di && missing.All(c => di.Copies.Any(x => x.UserAssetId == c.UserAssetId));
            if (!atDestination && step.State == StepState.Suggested) continue; // a mere suggestion: the next build drops it
            bool wasOpened = step.State == StepState.Opened;
            step.State = StepState.NeedsCheck;
            step.Attention = atDestination
                ? wasOpened
                    ? $"As cópias já estão em @{step.DestinationName}: a trade parece concluída. Use \"Localizar trade no Roblox\" para conferir."
                    : $"As cópias já estão em @{step.DestinationName}: a trade pode ter sido feita fora do RbxSwitcher. Localize a trade para conferir."
                : $"{string.Join(", ", missing.Select(c => c.Label))} saiu de @{step.SourceName} sem esta etapa. Confira antes de seguir.";
            step.Log(input.Now, "em conferência: " + step.Attention);
        }
        foreach (var step in plan.Steps.Where(x => x.State == StepState.Opened))
        {
            var problems = ReadyCheck(step, input, plan).Where(p => !p.Contains("há mais de 30 minutos")).ToList();
            if (problems.Count == 0) continue;
            step.State = StepState.NeedsCheck;
            step.Attention = "A composição mudou desde que a trade foi aberta: " + string.Join("; ", problems) + ". Confira antes de enviar.";
            step.Log(input.Now, "em conferência: composição mudou");
        }
    }

    /// <summary>
    /// The sum behind a destination's space, so it can be checked: teto − ocupado, and what each is made of (3 lines).
    /// "Ocupado" already holds the balance, the pending Robux and the Limiteds waiting for sale in the account.
    /// </summary>
    public static IEnumerable<string> SpaceSum(string username, SpaceView v, DistributionSettings s)
    {
        bool atSale = s.UseSpaceAtSale;
        long? ceiling = atSale ? v.CeilingAtSale : v.Ceiling;
        long? space = atSale ? v.SpaceAtSale : v.SpaceNow;
        int stockCopies = v.Copies - v.Smalls;
        yield return $"Espaço de @{username} {(atSale ? "na venda" : "agora")}: teto {Fmt(ceiling ?? 0)} − ocupado {Fmt(v.Occupied ?? 0)} = {Fmt(space ?? 0)}" +
                     (atSale ? $" (agora: {Fmt(v.SpaceNow ?? 0)})." : ".");
        yield return $"Ocupado em @{username}: saldo {Fmt(v.Balance ?? 0)} + pendentes {Fmt(v.Pending ?? 0)} + Limiteds parados {Fmt(v.StockNet ?? 0)}" +
                     (stockCopies > 0 ? $" ({P(stockCopies, "cópia", "cópias")}, líquido estimado)" : "") +
                     (v.Committed > 0 ? $" + lotes a caminho {Fmt(v.Committed)}" : "") + ".";
        yield return $"Teto de @{username}: cota de 30 dias que sobra {Fmt(v.RemainingMonthly ?? 0)}" +
                     (atSale ? $" + {Fmt(v.ReleasedBySale ?? 0)} que a janela libera em {P(s.DaysToSale, "dia", "dias")}" : "") +
                     (v.Target > 0 ? $", limitado pela meta {Fmt(v.Target)}" : "") + ".";
    }

    private static string HeldNote(ValuedCopy v) => v.OnHold ? "em holding; " : "";

    private static string SourceText(PriceSource s) => s switch { PriceSource.Manual => "preço manual", PriceSource.Rolimons => "valor Rolimons", _ => "RAP" };

    private static string P(long n, string one, string many) => $"{Fmt(n)} {(n == 1 ? one : many)}";

    internal static string Fmt(long n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    private static string Brief(IEnumerable<string> items)
    {
        var list = items.ToList();
        return string.Join(", ", list.Take(3)) + (list.Count > 3 ? $" e mais {list.Count - 3}" : "");
    }

    private sealed class Dest(AccountSnapshot a)
    {
        public AccountSnapshot A { get; } = a;
        public string? Blocked { get; set; }
        public long Before { get; set; }
        public long Remaining { get; set; }
        public List<ValuedCopy> Smalls { get; set; } = [];
        /// <summary>Smalls another trade of this plan brings (not there yet).</summary>
        public HashSet<long> Incoming { get; } = [];
        public SpaceView? View { get; set; }
    }
}
