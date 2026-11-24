using System.Text;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Finance;

public enum IdentityMatch { Matches, OtherAccount, Refused, NoAnswer, NotAvailable }

public sealed record IdentityProbe(string Source, int? Http, IdentityMatch Match);

/// <param name="Shape">Field names and types only (<see cref="JsonShape"/>), never values.</param>
/// <param name="PlusFields">For each Plus record: where it was and which field each value came from (names only).</param>
public sealed record SubscriptionPageProbe(int Page, int? Http, string? Shape, int PlusEntries, string? NextPageField, string? ParserError,
                                           IReadOnlyList<string>? PlusFields = null);

public sealed record PlusDiagnosticReport(
    IReadOnlyList<IdentityProbe> Identity,
    string? SessionUsed,
    IReadOnlyList<SubscriptionPageProbe> Pages,
    PlusInfo? Parsed,
    string? Rejected);

/// <summary>
/// Reproduces the Plus read step by step with the account's own session, to find which field or format
/// the parser rejects. The report holds HTTP statuses, field names and types, counts and the parser's
/// verdict: no cookie, no response value, no payment data.
/// </summary>
public static class PlusDiagnostics
{
    public const string VaultSource = "sessão salva no cofre";
    public const string BrowserSource = "sessão do navegador do app";

    /// <param name="browserSession">Cookie read from the account's open tab, or null when the tab is not open.</param>
    public static async Task<PlusDiagnosticReport> RunAsync(RobloxApi api, long expectedUserId, string vaultSession, string? browserSession,
                                                            DateTimeOffset now, CancellationToken ct = default)
    {
        // 1. Identity: who each session belongs to, compared with the account opened in the app.
        var identity = new List<IdentityProbe> { await Probe(api, VaultSource, vaultSession, expectedUserId, ct) };
        identity.Add(browserSession is null
            ? new IdentityProbe(BrowserSource, null, IdentityMatch.NotAvailable)
            : await Probe(api, BrowserSource, browserSession, expectedUserId, ct));

        // The browser session is the one that shows the official page; fall back to the saved one.
        string? session = null, used = null;
        foreach (var (src, s) in new[] { (BrowserSource, browserSession), (VaultSource, (string?)vaultSession) })
            if (s is not null && identity.First(i => i.Source == src).Match == IdentityMatch.Matches) { session = s; used = src; break; }
        if (session is null)
            return new PlusDiagnosticReport(identity, null, [], null, "nenhuma sessão confirmada para esta conta: nada foi consultado");

        // 2. Every page of subscriptions, as the parser sees it.
        var pages = new List<SubscriptionPageProbe>();
        var items = new List<PlusItem>();
        PageCursor? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool filtered = true;
        bool? eligible = null;
        for (int n = 1; n <= RobloxApi.MaxPlusPages; n++)
        {
            var res = await api.ProbePlusPageAsync(session, cursor, ct, filtered).ConfigureAwait(false);
            if (res.RotatedSession is { } rotated) session = rotated;
            if (!res.Ok && res.Http == 400 && filtered && cursor is null)
            {
                // Same fallback as the app: the ProductType filter refused, ask again without it.
                pages.Add(new SubscriptionPageProbe(n, res.Http, null, 0, null, res.Problem + " — repetindo sem o filtro ProductType"));
                filtered = false;
                continue;
            }
            if (!res.Ok)
            {
                pages.Add(new SubscriptionPageProbe(n, res.Http, null, 0, null, res.Problem));
                return new PlusDiagnosticReport(identity, used, pages, null, res.Problem);
            }
            var (shape, page, error) = res.Value!;
            var fields = page?.Items.Select(i =>
                $"registro em {i.EntryPath}; campos usados: " +
                (i.FieldPaths is { Count: > 0 } fp ? string.Join(", ", fp.Select(kv => $"{kv.Key} ← {kv.Value}")) : "nenhum (sem status e sem data)") +
                (i.Notes is { Count: > 0 } ns ? "; observações: " + string.Join("; ", ns) : "")).ToList();
            pages.Add(new SubscriptionPageProbe(n, res.Http, shape, page?.Items.Count ?? 0, page?.NextCursor?.Param, error, fields));
            if (page is null)
                return new PlusDiagnosticReport(identity, used, pages, null, error);
            items.AddRange(page.Items);
            eligible ??= page.TrialEligible;
            cursor = page.NextCursor;
            if (cursor is null) break;
            if (!seen.Add(cursor.Param + "=" + cursor.Value))
                return new PlusDiagnosticReport(identity, used, pages, null, "paginação de assinaturas repetida");
        }
        if (cursor is not null)
            return new PlusDiagnosticReport(identity, used, pages, null, "assinaturas com páginas demais");

        // 3. The decision the app would make.
        try
        {
            var plus = EconomyParsers.DecidePlus(items, now);
            plus.TrialEligible = eligible;
            return new PlusDiagnosticReport(identity, used, pages, plus, null);
        }
        catch (FormatException e) { return new PlusDiagnosticReport(identity, used, pages, null, e.Message); }
    }

    private static async Task<IdentityProbe> Probe(RobloxApi api, string source, string session, long expected, CancellationToken ct)
    {
        var r = await api.GetAuthenticatedUserAsync(session, ct).ConfigureAwait(false);
        return r.Outcome switch
        {
            AuthOutcome.Valid when r.User!.Id == expected => new IdentityProbe(source, 200, IdentityMatch.Matches),
            AuthOutcome.Valid => new IdentityProbe(source, 200, IdentityMatch.OtherAccount),
            AuthOutcome.Invalid => new IdentityProbe(source, 401, IdentityMatch.Refused),
            _ => new IdentityProbe(source, null, IdentityMatch.NoAnswer),
        };
    }

    /// <summary>Plain text for the screen and the local log. Dates in Brasília time.</summary>
    public static string Format(PlusDiagnosticReport r, string account, DateTimeOffset now, string appVersion)
    {
        var br = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        string D(DateTimeOffset t) => BrasiliaTime.ToBrasilia(t).ToString("dd/MM/yyyy HH:mm", br);
        var sb = new StringBuilder();
        sb.AppendLine($"Diagnóstico Plus — @{account} — {D(now)} (Brasília) — RbxSwitcher {appVersion}");
        sb.AppendLine("1. Identidade (comparada com a conta aberta no app)");
        foreach (var i in r.Identity)
        {
            string result = i.Match switch
            {
                IdentityMatch.Matches => "conta confere",
                IdentityMatch.OtherAccount => "pertence a OUTRA conta",
                IdentityMatch.Refused => "sessão recusada",
                IdentityMatch.NoAnswer => "Roblox não respondeu",
                _ => "aba da conta não está aberta",
            };
            sb.AppendLine($"   {i.Source}: " + (i.Http is { } h ? $"HTTP {h}, " : "") + result);
        }
        sb.AppendLine("2. Assinaturas" + (r.SessionUsed is { } u ? $" (consultadas com a {u})" : ""));
        if (r.Pages.Count == 0) sb.AppendLine("   não consultadas");
        foreach (var p in r.Pages)
        {
            sb.AppendLine($"   Página {p.Page}: " + (p.Http is { } h ? $"HTTP {h}" : "sem resposta") +
                          $"; itens de Plus encontrados: {p.PlusEntries}; próxima página: {(p.NextPageField is { } f ? "sim (" + f + ")" : "não")}");
            if (p.Shape is not null) sb.AppendLine($"   Campos: {p.Shape}");
            foreach (var line in p.PlusFields ?? []) sb.AppendLine($"   Plus: {line}");
            if (p.ParserError is not null) sb.AppendLine($"   Recusado nesta página: {p.ParserError}");
        }
        sb.AppendLine("3. Leitura do parser");
        if (r.Rejected is not null) sb.AppendLine($"   RECUSADA: {r.Rejected} → Plus fica \"A conferir\"");
        else if (r.Parsed is { } plus)
        {
            string status = plus.NoSubscription ? "Sem Plus" : !plus.Active ? "Expirado" : plus.Trial ? "Trial" : "Ativo";
            sb.AppendLine("   Teste grátis: " + (plus.TrialEligible switch { true => "disponível (informado pelo Roblox)", false => "indisponível (informado pelo Roblox)", _ => "não informado nesta resposta" }));
            sb.AppendLine($"   ACEITA: {status}" +
                          (plus.ExpiresUtc is { } e ? $"; validade {D(e)}" : "; validade não informada") +
                          (plus.RenewsUtc is { } rn ? $"; renovação {D(rn)}" : "; renovação não informada") +
                          $"; renovação confirmada: {(plus.WillRenew switch { true => "sim", false => "não (cancelada)", _ => "não informada" })}" +
                          (plus.Notes.Count > 0 ? "; observações: " + string.Join("; ", plus.Notes) : ""));
        }
        sb.Append("Sem cookies, sem valores da resposta e sem dados de pagamento: só status HTTP, nomes e tipos de campos.");
        return sb.ToString();
    }
}
