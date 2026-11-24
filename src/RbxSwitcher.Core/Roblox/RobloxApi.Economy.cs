using System.Net;
using System.Text.Json;
using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.Core.Roblox;

public enum ReadOutcome { Ok, Unauthorized, RateLimited, Failed }

/// <summary>Result of one authenticated read. <see cref="Value"/> is set only when <see cref="Outcome"/> is Ok.</summary>
public sealed record ReadResult<T>(ReadOutcome Outcome, T? Value, string? RotatedSession = null, TimeSpan? RetryAfter = null, string? Problem = null)
{
    public bool Ok => Outcome == ReadOutcome.Ok;
    /// <summary>HTTP status of the response (null when Roblox could not be reached).</summary>
    public int? Http { get; init; }
}

/// <param name="Ids">Every id-like field of the record (idHash, transactionId, id, purchaseToken) by name.
/// Which one is the stable per-transaction id is decided and verified by the ledger, not assumed.</param>
public sealed record TransferRecord(IReadOnlyDictionary<string, string> Ids, DateTimeOffset CreatedUtc, long Amount, string Role)
{
    /// <summary>Sent by this account: role Sender and a negative amount.</summary>
    public bool IsOutgoing => string.Equals(Role, "Sender", StringComparison.OrdinalIgnoreCase) && Amount < 0;
}

public sealed record PageCursor(string Param, string Value)
{
    public string AppendTo(string url) => url + (url.Contains('?') ? "&" : "?") + Param + "=" + Uri.EscapeDataString(Value);
}

public sealed record TransferPage(IReadOnlyList<TransferRecord> Items, PageCursor? NextCursor);

/// <summary>One Plus subscription entry (product RobloxPlus or its internal name Blackbird).</summary>
/// <param name="EntryPath">Where the subscription record was found (e.g. "subscriptions[]"), field names only.</param>
/// <param name="FieldPaths">Which response field each value came from (e.g. "validade" → "subscriptions[].expirationTimestampMs").</param>
public sealed record PlusItem(string Product, string? Status, DateTimeOffset? ExpiresUtc,
                              DateTimeOffset? RenewsUtc = null, bool? WillRenew = null, bool? Trial = null,
                              string EntryPath = "", IReadOnlyDictionary<string, string>? FieldPaths = null,
                              IReadOnlyList<string>? Notes = null);

/// <param name="TrialEligible">Only set when the response itself has a boolean eligibility field (e.g. isTrialEligible).</param>
public sealed record PlusPage(IReadOnlyList<PlusItem> Items, PageCursor? NextCursor, bool? TrialEligible = null);

/// <summary>Read-only economy endpoints. All are GETs on *.roblox.com with the account's own cookie.</summary>
public sealed partial class RobloxApi
{
    internal const string CurrencyUrl = "https://economy.roblox.com/v1/user/currency";
    internal const string PendingUrl = "https://economy.roblox.com/v2/users/{0}/transaction-totals?timeFrame=Month&transactionType=Summary";
    internal const string PlusUrl = "https://apis.roblox.com/subscriptions/v2/user/subscriptions?ProductType=RobloxPlus";
    internal const string PlusUrlUnfiltered = "https://apis.roblox.com/subscriptions/v2/user/subscriptions";
    internal const string TransferLimitUrl = "https://apis.roblox.com/transfer/v1/robux-transfer/user-transfer-limit";
    internal const string TransferSettingsUrl = "https://apis.roblox.com/user-settings-api/v2/user-settings/settings-and-options-subset?requestedUserSettings=robuxTransferLimits";
    internal const string TransactionsUrl = "https://apis.roblox.com/transaction-records/v1/users/{0}/transactions?transactionType=CurrencyTransfer&limit=100&sortOrder=Desc";
    internal const int MaxPlusPages = 10;

    public Task<ReadResult<long>> GetRobuxAsync(string session, CancellationToken ct = default) =>
        GetJsonAsync(CurrencyUrl, session, EconomyParsers.Robux, null, ct);

    public Task<ReadResult<long>> GetPendingRobuxAsync(string session, long userId, CancellationToken ct = default) =>
        GetJsonAsync(string.Format(PendingUrl, userId), session, EconomyParsers.Pending, null, ct);

    /// <summary>
    /// Reads every page of subscriptions before deciding (Plus may be on any page). Logs the HTTP status
    /// and the field names/types of each page locally, never values.
    /// </summary>
    public async Task<ReadResult<PlusInfo>> GetPlusAsync(string session, DateTimeOffset now, CancellationToken ct = default)
    {
        var items = new List<PlusItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        PageCursor? cursor = null;
        string? rotated = null;
        bool? trialEligible = null;
        string baseUrl = PlusUrl;
        for (int page = 0; page < MaxPlusPages; page++)
        {
            string url = cursor?.AppendTo(baseUrl) ?? baseUrl;
            var res = await GetJsonAsync(url, session, EconomyParsers.PlusPage, $"assinaturas p{page + 1}", ct).ConfigureAwait(false);
            if (res.RotatedSession is { } r) { session = r; rotated = r; }
            if (!res.Ok && res.Http == 400 && page == 0 && baseUrl == PlusUrl)
            {
                // Accounts without any subscription answered 400 to the ProductType filter. Same official endpoint,
                // without the filter: only a valid list with no Plus entry may say "Sem Plus".
                Logging.Log.Warn($"[assinaturas] {res.Problem} com ProductType=RobloxPlus; repetindo sem o filtro");
                baseUrl = PlusUrlUnfiltered;
                page = -1;
                continue;
            }
            if (!res.Ok) return new ReadResult<PlusInfo>(res.Outcome, null, rotated, res.RetryAfter, res.Problem);
            items.AddRange(res.Value!.Items);
            if (res.Value.TrialEligible is { } te)
            {
                if (trialEligible is { } prev && prev != te)
                    return new ReadResult<PlusInfo>(ReadOutcome.Failed, null, rotated, Problem: "assinaturas: elegibilidade ao teste grátis contraditória entre páginas");
                trialEligible = te;
            }
            cursor = res.Value.NextCursor;
            if (cursor is null)
            {
                foreach (var it in items)
                    Logging.Log.Info($"[assinaturas] registro em {it.EntryPath}; campos usados: " +
                        (it.FieldPaths is { Count: > 0 } fp ? string.Join(", ", fp.Select(kv => $"{kv.Key} ← {kv.Value}")) : "nenhum") +
                        (it.Notes is { Count: > 0 } ns ? "; observações: " + string.Join("; ", ns) : ""));
                try
                {
                    var info = EconomyParsers.DecidePlus(items, now);
                    info.TrialEligible = trialEligible;
                    if (baseUrl != PlusUrl) info.Notes.Add("lido sem o filtro ProductType (o filtro respondeu HTTP 400)");
                    return new ReadResult<PlusInfo>(ReadOutcome.Ok, info, rotated);
                }
                catch (FormatException e)
                {
                    Logging.Log.Warn($"[assinaturas] decisão recusada: {e.Message}");
                    return new ReadResult<PlusInfo>(ReadOutcome.Failed, null, rotated, Problem: e.Message);
                }
            }
            if (!seen.Add(cursor.Param + "=" + cursor.Value))
                return new ReadResult<PlusInfo>(ReadOutcome.Failed, null, rotated, Problem: "paginação de assinaturas repetida");
        }
        return new ReadResult<PlusInfo>(ReadOutcome.Failed, null, rotated, Problem: "assinaturas com páginas demais");
    }

    /// <summary>
    /// One subscriptions page for the diagnostic: never throws on format; returns the field shape, the parsed
    /// page (or null) and the parser's refusal reason.
    /// </summary>
    public Task<ReadResult<(string Shape, PlusPage? Page, string? Error)>> ProbePlusPageAsync(string session, PageCursor? cursor, CancellationToken ct = default, bool filtered = true)
    {
        string baseUrl = filtered ? PlusUrl : PlusUrlUnfiltered;
        string url = cursor?.AppendTo(baseUrl) ?? baseUrl;
        return GetJsonAsync(url, session, r =>
        {
            string shape = Logging.JsonShape.Describe(r);
            try { return (shape, (PlusPage?)EconomyParsers.PlusPage(r), (string?)null); }
            catch (FormatException e) { return (shape, null, e.Message); }
        }, null, ct);
    }

    public Task<ReadResult<TransferCaps>> GetTransferCapsAsync(string session, CancellationToken ct = default) =>
        GetJsonAsync(TransferLimitUrl, session, EconomyParsers.Caps, null, ct);

    /// <summary>Ok with null value = no restriction configured.</summary>
    public Task<ReadResult<TransferRestriction?>> GetTransferRestrictionAsync(string session, CancellationToken ct = default) =>
        GetJsonAsync(TransferSettingsUrl, session, EconomyParsers.Restriction, null, ct);

    /// <summary>The first page of a crawl always logs its shape; later pages only when they fail.</summary>
    public Task<ReadResult<TransferPage>> GetTransfersPageAsync(string session, long userId, PageCursor? cursor, CancellationToken ct = default)
    {
        string url = string.Format(TransactionsUrl, userId);
        if (cursor is not null) url = cursor.AppendTo(url);
        return GetJsonAsync(url, session, EconomyParsers.TransferPage, cursor is null ? "histórico p1" : null, ct, logOnFailure: "histórico");
    }

    /// <summary>
    /// Roblox's own explanation in an error body ({"errors":[{"code","message"}]} or {"code","message"}),
    /// shortened and cleaned: long digit runs (ids) removed, secrets redacted. Null when there is none.
    /// </summary>
    private static async Task<string?> ErrorReasonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length == 0 || body.Length > 16_384) return null;
            using var doc = JsonDocument.Parse(body);
            return EconomyParsers.ErrorReason(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// GET with the account's cookie. A parser throws <see cref="FormatException"/> with a Portuguese,
    /// value-free reason (which field, what was wrong); it becomes the read's <c>Problem</c>.
    /// <paramref name="shapeTag"/>: log status + field names/types of every response (local log only).
    /// </summary>
    private async Task<ReadResult<T>> GetJsonAsync<T>(string url, string session, Func<JsonElement, T> parse, string? shapeTag,
                                                      CancellationToken ct, string? logOnFailure = null)
    {
        if (!LooksLikeSession(session))
            return new ReadResult<T>(ReadOutcome.Unauthorized, default, Problem: "sessão inválida");
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only https://*.roblox.com may receive a session.");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Add("Cookie", $"{CookieName}={session}");
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ReadResult<T>(ReadOutcome.Failed, default, Problem: "sem conexão com o Roblox");
        }
        using (resp)
        {
            string? rotated = ReadRotatedSession(resp.Headers, session);
            int code = (int)resp.StatusCode;
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                if (shapeTag is not null || logOnFailure is not null)
                    Logging.Log.Warn($"[{shapeTag ?? logOnFailure}] HTTP {code}");
                return resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new ReadResult<T>(ReadOutcome.Unauthorized, default, rotated, Problem: "sessão recusada (HTTP 401)") { Http = code },
                    HttpStatusCode.TooManyRequests => new ReadResult<T>(ReadOutcome.RateLimited, default, rotated,
                        resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30), "limite de requisições do Roblox (HTTP 429)") { Http = code },
                    _ => new ReadResult<T>(ReadOutcome.Failed, default, rotated,
                        Problem: $"Roblox respondeu HTTP {code}" + (await ErrorReasonAsync(resp, ct).ConfigureAwait(false) is { } why ? $" ({why})" : "")) { Http = code },
                };
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); }
            catch (JsonException)
            {
                Logging.Log.Warn($"[{shapeTag ?? logOnFailure ?? uri.AbsolutePath}] HTTP 200, corpo não é JSON");
                return new ReadResult<T>(ReadOutcome.Failed, default, rotated, Problem: "resposta não é JSON (HTTP 200)") { Http = code };
            }
            using (doc)
            {
                if (shapeTag is not null)
                    Logging.Log.Info($"[{shapeTag}] HTTP 200; campos: {Logging.JsonShape.Describe(doc.RootElement)}");
                try
                {
                    return new ReadResult<T>(ReadOutcome.Ok, parse(doc.RootElement), rotated) { Http = code };
                }
                catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    string reason = e is FormatException ? e.Message : "estrutura inesperada";
                    if (shapeTag is null)
                        Logging.Log.Warn($"[{logOnFailure ?? uri.AbsolutePath}] HTTP 200; campos: {Logging.JsonShape.Describe(doc.RootElement)}");
                    Logging.Log.Warn($"[{shapeTag ?? logOnFailure ?? uri.AbsolutePath}] leitura recusada: {reason}");
                    return new ReadResult<T>(ReadOutcome.Failed, default, rotated, Problem: reason) { Http = code };
                }
            }
        }
    }
}

/// <summary>
/// All knowledge of the response shapes lives here. Every refusal throws a FormatException whose
/// message names the field and the problem in Portuguese, without any value from the response.
/// </summary>
public static class EconomyParsers
{
    public static long Robux(JsonElement r) => RequireLong(r, "robux");

    public static long Pending(JsonElement r) => RequireLong(r, "pendingRobuxTotal");

    public static TransferCaps Caps(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) throw new FormatException("tetos: a resposta não é um objeto");
        if (!r.TryGetProperty("dailyLimit", out _) && !r.TryGetProperty("monthlyLimit", out _))
            throw new FormatException("tetos: campos dailyLimit e monthlyLimit ausentes");
        return new TransferCaps
        {
            Daily = OptionalLong(r, "dailyLimit"),
            Monthly = OptionalLong(r, "monthlyLimit"),
            PerTransfer = OptionalLong(r, "perTransferLimit"),
        };
    }

    public static TransferRestriction? Restriction(JsonElement r)
    {
        var setting = FindProperty(r, "robuxTransferLimits", depth: 4)
                      ?? throw new FormatException("restrições: campo robuxTransferLimits ausente");
        if (setting.ValueKind == JsonValueKind.Null) return null;
        if (!setting.TryGetProperty("currentValue", out var current) || current.ValueKind == JsonValueKind.Null)
            return null;
        if (current.ValueKind != JsonValueKind.Object) throw new FormatException("restrições: currentValue não é um objeto");
        long? daily = OptionalLong(current, "daily"), monthly = OptionalLong(current, "monthly");
        return daily is null && monthly is null ? null : new TransferRestriction { Daily = daily, Monthly = monthly };
    }

    // ─────────────── Plus ───────────────

    /// <summary>Product names that mean Roblox Plus: the public one and the internal "Blackbird".</summary>
    private static readonly string[] PlusProducts = ["RobloxPlus", "Blackbird"];
    private static readonly string[] ProductFields = ["productType", "ProductType", "subscriptionProductType", "productName", "product"];
    private static readonly string[] ListFields = ["subscriptions", "data", "userSubscriptions", "items", "results"];

    /// <summary>
    /// One page of subscriptions. Plus entries are found wherever they are in the response (any list,
    /// nested "product" objects); "no Plus here" is only claimed when the subscription list itself was found.
    /// </summary>
    public static PlusPage PlusPage(JsonElement r)
    {
        var items = new List<PlusItem>();
        FindPlus(r, 0, "", null, "", false, items);
        bool listFound = r.ValueKind == JsonValueKind.Array || ListFields.Any(f => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.Array);
        if (items.Count == 0 && !listFound)
            throw new FormatException("assinaturas: lista de assinaturas não encontrada");
        bool? eligible = null;
        var flags = new List<Hit>();
        CollectAll(r, "", 0, TrialEligibleFields, flags);
        foreach (var h in flags)
        {
            bool b = h.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => eligible ?? false,
                _ => throw new FormatException($"assinaturas: campo {h.Name} não é verdadeiro/falso"),
            };
            if (h.Value.ValueKind == JsonValueKind.Null) continue;
            if (eligible is { } prev && prev != b) throw new FormatException("assinaturas: campos de elegibilidade ao teste grátis contraditórios");
            eligible = b;
        }
        return new PlusPage(items, NextCursor(r), eligible);
    }

    /// <summary>
    /// Free-trial eligibility in any JSON (e.g. a response of the official Plus page): true/false when the
    /// recognized boolean fields agree, null when absent or contradictory. Payment data is skipped.
    /// </summary>
    public static bool? TrialEligibleIn(JsonElement r)
    {
        var hits = new List<Hit>();
        CollectAll(r, "", 0, TrialEligibleFields, hits);
        if (hits.Count == 0) CollectWhere(r, 0, IsTrialEligibilityName, hits);
        var values = hits.Where(h => h.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                         .Select(h => h.Value.ValueKind == JsonValueKind.True).Distinct().ToList();
        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>
    /// A field name about being ABLE to get a trial (eligible / available / offer / can…), not about being IN one
    /// ("isTrial", "inTrialPeriod" stay out).
    /// </summary>
    public static bool IsTrialEligibilityName(string name)
    {
        string n = name.ToLowerInvariant();
        if (!n.Contains("trial")) return false;
        return n.Contains("elig") || n.Contains("avail") || n.Contains("offer") || n.StartsWith("can") || n.StartsWith("show") || n.Contains("redeemable");
    }

    private static void CollectWhere(JsonElement e, int depth, Func<string, bool> match, List<Hit> hits)
    {
        if (depth > 6) return;
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) CollectWhere(x, depth + 1, match, hits); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            if (IsPrivateKey(p.Name)) continue;
            if (match(p.Name)) hits.Add(new Hit(p.Name, p.Name, p.Value));
            CollectWhere(p.Value, depth + 1, match, hits);
        }
    }

    /// <summary>Boolean fields that say the account may start a free trial. Read only when present.</summary>
    private static readonly string[] TrialEligibleFields =
        ["isTrialEligible", "trialEligible", "isEligibleForTrial", "eligibleForTrial", "isFreeTrialEligible", "freeTrialEligible",
         "isEligibleForFreeTrial", "eligibleForFreeTrial", "canStartTrial", "canStartFreeTrial", "hasTrialOffer", "hasFreeTrialOffer"];

    /// <summary>Like Collect, but through arrays too (eligibility may sit on a product/offer list).</summary>
    private static void CollectAll(JsonElement e, string path, int depth, string[] names, List<Hit> hits)
    {
        if (depth > 6) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) CollectAll(x, path + "[]", depth + 1, names, hits);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            if (IsPrivateKey(p.Name)) continue;
            string here = path.Length == 0 ? p.Name : path + "." + p.Name;
            if (names.Contains(p.Name)) hits.Add(new Hit(p.Name, here, p.Value));
            CollectAll(p.Value, here, depth + 1, names, hits);
        }
    }

    /// <summary>Roblox error body → "código 3: mensagem" (no ids, no secrets, at most 140 characters).</summary>
    public static string? ErrorReason(JsonElement r)
    {
        JsonElement err = r;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            err = list[0];
        if (err.ValueKind != JsonValueKind.Object) return null;
        string? code = StringOf(err, "code", "errorCode");
        string? message = StringOf(err, "message", "userFacingMessage", "error", "errorMessage");
        if (code is null && message is null) return null;
        if (message is not null && message.TrimStart().StartsWith('{'))
        {
            // Roblox sometimes puts a JSON document in the message ({"ValidationErrors":[…]}): keep only the error names.
            var names = Avatars.AvatarParsers.WearValidation(r).Select(v => v.Message).Where(m => m.Length > 0).Distinct().ToList();
            message = names.Count > 0 ? string.Join(", ", names) : "detalhes em formato técnico";
        }
        if (message is not null)
        {
            message = System.Text.RegularExpressions.Regex.Replace(Security.Redact.Text(message), @"\d{6,}", "<n>");
            message = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ").Trim();
            if (message.Length > 140) message = message[..140] + "…";
        }
        return code is not null && message is { Length: > 0 } ? $"código {code}: {message}" : code is not null ? $"código {code}" : message;
    }

    /// <summary>
    /// Walks the response; <paramref name="entry"/> is the nearest list item above (the subscription record).
    /// The product type may sit on the record itself or in a nested object ("productInfo": {"productType": …});
    /// either way status and dates are read from the whole record, not only from where the product name was.
    /// </summary>
    /// <param name="inSubscriptionList">The record is an item of a subscription list (subscriptions, data, … or a
    /// root array). Elsewhere (e.g. "offers") a Plus product only counts if the record has subscription fields;
    /// otherwise it is an offer or a catalog entry, not a subscription.</param>
    private static void FindPlus(JsonElement e, int depth, string path, JsonElement? entry, string entryPath, bool inSubscriptionList, List<PlusItem> found)
    {
        if (depth > 6) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            string listName = path.Split('.').Last().Replace("[]", "");
            bool subscriptionList = path.Length == 0 || ListFields.Contains(listName);
            foreach (var x in e.EnumerateArray()) FindPlus(x, depth + 1, path + "[]", x, path + "[]", subscriptionList, found);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (ProductOf(e) is { } product && PlusProducts.Contains(product, StringComparer.OrdinalIgnoreCase))
        {
            var record = entry ?? e;
            string recordPath = entry is null ? (path.Length == 0 ? "(raiz)" : path) : entryPath;
            if (inSubscriptionList || entry is null || HasSubscriptionFields(record, recordPath))
                found.Add(ToPlusItem(record, recordPath, product));
            return;
        }
        foreach (var p in e.EnumerateObject())
            if (!IsPrivateKey(p.Name))
                FindPlus(p.Value, depth + 1, path.Length == 0 ? p.Name : path + "." + p.Name, entry, entryPath, inSubscriptionList, found);
    }

    // A property, not a field: static fields initialize in file order and the lists are declared further down.
    private static string[] SubscriptionMarkers =>
        [.. StatusFieldsList, "expirationTimestampMs", .. ExpiryIsoFieldsList, .. RenewalMsFieldsList, .. RenewalIsoFieldsList, "subscriptionId"];

    private static bool HasSubscriptionFields(JsonElement record, string recordPath)
    {
        var hits = new List<Hit>();
        Collect(record, recordPath, 0, SubscriptionMarkers, hits);
        return hits.Any(h => h.Value.ValueKind != JsonValueKind.Null);
    }

    /// <summary>Payment data is never read, so a card's expiry can never be taken for the Plus validity.</summary>
    private static bool IsPrivateKey(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("payment") || n.Contains("card") || n.Contains("instrument") || n.Contains("wallet") || n.Contains("billingaddress");
    }

    private static string? ProductOf(JsonElement item)
    {
        foreach (var f in ProductFields)
        {
            if (!item.TryGetProperty(f, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Object && StringOf(v, "type", "productType", "name") is { } nested) return nested;
        }
        return null;
    }

    // Candidate field names. Each is read only if present; a present field with an unexpected type is refused by name.
    private static readonly string[] StatusFieldsList = ["status", "subscriptionStatus", "state"];
    private static readonly string[] StatusFields = StatusFieldsList;
    private static readonly string[] ExpiryIsoFieldsList = ["expiration", "expirationDate", "expiresAt", "expirationTime", "currentPeriodEnd"];
    private static readonly string[] ExpiryIsoFields = ExpiryIsoFieldsList;
    private static readonly string[] RenewalMsFieldsList = ["renewalTimestampMs", "nextRenewalTimestampMs", "nextBillingTimestampMs", "renewalDateTimestampMs"];
    private static readonly string[] RenewalMsFields = RenewalMsFieldsList;
    private static readonly string[] RenewalIsoFieldsList = ["renewalDate", "nextRenewalDate", "nextBillingDate", "renewsAt", "renewalTime"];
    private static readonly string[] RenewalIsoFields = RenewalIsoFieldsList;
    private static readonly string[] WillRenewFields = ["willRenew", "autoRenew", "isAutoRenew", "isAutoRenewing", "autoRenewEnabled", "isRenewing", "renewalEnabled"];
    private static readonly string[] WontRenewFields = ["cancelAtPeriodEnd", "isCancelled", "isCanceled"];
    private static readonly string[] TrialFields = ["isTrial", "isInTrial", "inTrialPeriod", "isFreeTrial", "isTrialPeriod", "trial"];

    private sealed record Hit(string Name, string Path, JsonElement Value);

    /// <summary>
    /// Finds a field anywhere in the subscription record (up to 3 objects deep, payment data excluded).
    /// Names are tried in order; the same field in more than one place with different values is refused.
    /// </summary>
    private static Hit? Lookup(JsonElement record, string recordPath, string[] names)
    {
        var hits = new List<Hit>();
        Collect(record, recordPath, 0, names, hits);
        foreach (var n in names)
        {
            var same = hits.Where(h => h.Name == n && h.Value.ValueKind != JsonValueKind.Null).ToList();
            if (same.Count == 0) continue;
            if (same.Select(h => h.Value.GetRawText()).Distinct().Count() > 1)
                throw new FormatException($"assinatura: campo {n} aparece mais de uma vez com valores diferentes");
            return same.OrderBy(h => h.Path.Length).First();
        }
        return null;
    }

    private static void Collect(JsonElement e, string path, int depth, string[] names, List<Hit> hits)
    {
        if (depth > 3 || e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            if (IsPrivateKey(p.Name)) continue;
            string here = path + "." + p.Name;
            if (names.Contains(p.Name)) hits.Add(new Hit(p.Name, here, p.Value));
            if (p.Value.ValueKind == JsonValueKind.Object) Collect(p.Value, here, depth + 1, names, hits);
        }
    }

    /// <summary>
    /// Essential fields (status, validity) decide Ativo/Expirado: a problem there refuses the read ("A conferir").
    /// Auxiliary fields (renewal date, renewal flags, trial) only add detail: a problem there becomes a note and
    /// the value counts as "not reported" — it never blocks the subscription status.
    /// A timestamp of 0 is Roblox's "not set", never 1970.
    /// </summary>
    private static PlusItem ToPlusItem(JsonElement record, string recordPath, string product)
    {
        var used = new Dictionary<string, string>();
        var notes = new List<string>();

        // ── essential ──
        string? status = null;
        if (Lookup(record, recordPath, StatusFields) is { } st)
        {
            if (st.Value.ValueKind != JsonValueKind.String) throw new FormatException($"assinatura: campo {st.Name} não é texto");
            status = st.Value.GetString();
            used["status"] = st.Path;
        }
        DateTimeOffset? expires = null;
        if (Lookup(record, recordPath, ["expirationTimestampMs"]) is { } ms)
        {
            expires = Millis(ms);
            if (expires is null) notes.Add($"{ms.Name} veio vazio (0)");
            else used["validade"] = ms.Path;
        }
        if (expires is null && Lookup(record, recordPath, ExpiryIsoFields) is { } iso)
        {
            expires = Iso(iso);
            used["validade"] = iso.Path;
        }

        // ── auxiliary ──
        DateTimeOffset? renews = Auxiliary(notes, () =>
        {
            foreach (var name in RenewalMsFields)
                if (Lookup(record, recordPath, [name]) is { } rms)
                {
                    if (Millis(rms) is { } r) { used["renovação"] = rms.Path; return r; }
                    notes.Add($"{rms.Name} veio vazio (0): renovação não informada");
                }
            if (Lookup(record, recordPath, RenewalIsoFields) is { } riso) { used["renovação"] = riso.Path; return Iso(riso); }
            return (DateTimeOffset?)null;
        });
        bool? willRenew = Auxiliary(notes, () =>
        {
            bool? will = Bools(record, recordPath, WillRenewFields, used, "renova");
            if (Bools(record, recordPath, WontRenewFields, used, "não renova") is { } wont)
            {
                if (will is { } w && w == wont) throw new FormatException("campos de renovação contraditórios: renovação tratada como não informada");
                will = !wont;
            }
            return will;
        });
        bool? trial = Auxiliary(notes, () => Bools(record, recordPath, TrialFields, used, "teste"));
        return new PlusItem(product, status, expires, renews, willRenew, trial, recordPath, used, notes);
    }

    /// <summary>Runs an auxiliary read; a refusal becomes a note and the value "not reported".</summary>
    private static T? Auxiliary<T>(List<string> notes, Func<T?> read)
    {
        try { return read(); }
        catch (FormatException e)
        {
            notes.Add(e.Message.StartsWith("assinatura: ") ? e.Message["assinatura: ".Length..] : e.Message);
            return default;
        }
    }

    /// <returns>The instant, or null when the field is 0 (Roblox's "not set"). Negative or non-numeric is refused.</returns>
    private static DateTimeOffset? Millis(Hit h)
    {
        long ms = h.Value.ValueKind switch
        {
            JsonValueKind.Number when h.Value.TryGetInt64(out long l) => l,
            JsonValueKind.String when long.TryParse(h.Value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long l2) => l2,
            _ => throw new FormatException($"assinatura: campo {h.Name} não é número de milissegundos"),
        };
        if (ms == 0) return null;
        if (ms < 0) throw new FormatException($"assinatura: campo {h.Name} negativo");
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    private static DateTimeOffset Iso(Hit h) =>
        h.Value.ValueKind == JsonValueKind.String
            ? ParseDate(h.Value.GetString()!, $"assinatura: campo {h.Name} com data ilegível")
            : throw new FormatException($"assinatura: campo {h.Name} não é data em texto");

    /// <summary>Every listed flag present in the record must agree; a non-boolean is refused by name.</summary>
    private static bool? Bools(JsonElement record, string recordPath, string[] names, Dictionary<string, string> used, string label)
    {
        bool? value = null;
        foreach (var n in names)
        {
            if (Lookup(record, recordPath, [n]) is not { } h) continue;
            bool b = h.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException($"assinatura: campo {n} não é verdadeiro/falso"),
            };
            if (value is { } prev && prev != b) throw new FormatException("assinatura: campos de renovação ou teste contraditórios");
            value = b;
            used[label] = h.Path;
        }
        return value;
    }

    /// <summary>
    /// Decides Plus from every entry on every page:
    /// <list type="bullet">
    /// <item>Validity: expirationTimestampMs (or, without it, the next renewal date).</item>
    /// <item>Renewal confirmed: status Renewing, a renewal flag, or a future renewal date — unless cancelled.</item>
    /// <item>Trial: only when the status or a trial flag says so.</item>
    /// </list>
    /// Unknown status values, contradictions and entries without a usable date and status are refused
    /// (shown as "Plus a conferir" with the reason).
    /// </summary>
    public static PlusInfo DecidePlus(IReadOnlyList<PlusItem> items, DateTimeOffset now)
    {
        if (items.Count == 0)
            return new PlusInfo { Active = false, Status = "Sem assinatura", NoSubscription = true }; // valid list, every page read

        var decided = new List<PlusInfo>();
        foreach (var it in items)
        {
            var kind = it.Status is null ? PlusKind.Unknown : ClassifyPlus(it.Status);
            if (it.Status is not null && kind == PlusKind.Unknown)
                throw new FormatException("assinatura: valor de status desconhecido");

            bool? willRenew = it.WillRenew;
            var notes = new List<string>(it.Notes ?? []);
            if (kind == PlusKind.Cancelled)
            {
                if (willRenew == true) notes.Add("status cancelado e campo de renovação ativo: tratado como sem renovação");
                willRenew = false;
            }
            else if (kind == PlusKind.Renewing) willRenew ??= true;
            if (willRenew is null && it.RenewsUtc is { } r && r > now && kind != PlusKind.Ended) willRenew = true;

            DateTimeOffset? validUntil = it.ExpiresUtc ?? it.RenewsUtc;
            bool active;
            if (validUntil is { } until)
            {
                active = until > now;
                if (active && kind == PlusKind.Ended) throw new FormatException("assinatura: status encerrado com validade no futuro");
            }
            else
            {
                active = kind switch
                {
                    PlusKind.Active or PlusKind.Renewing or PlusKind.Trial => true,
                    PlusKind.Ended => false,
                    PlusKind.Cancelled => throw new FormatException("assinatura: cancelada sem data de validade"),
                    _ => throw new FormatException("assinatura: sem status e sem validade (expirationTimestampMs ausente ou vazio)"),
                };
            }
            bool trial = it.Trial == true || kind == PlusKind.Trial;
            if (it.Trial == false && kind == PlusKind.Trial) { notes.Add("status de teste e campo de teste discordam: teste não confirmado"); trial = false; }
            decided.Add(new PlusInfo
            {
                Active = active, Status = it.Status, ExpiresUtc = it.ExpiresUtc, RenewsUtc = it.RenewsUtc,
                WillRenew = willRenew, Trial = trial && active, Notes = notes,
            });
        }
        return decided.Where(d => d.Active).OrderByDescending(d => d.ExpiresUtc ?? d.RenewsUtc ?? DateTimeOffset.MaxValue).FirstOrDefault()
               ?? decided.OrderByDescending(d => d.ExpiresUtc ?? d.RenewsUtc ?? DateTimeOffset.MinValue).First();
    }

    private enum PlusKind { Unknown, Active, Renewing, Trial, Cancelled, Ended }

    private static PlusKind ClassifyPlus(string status) => status.Trim().ToLowerInvariant() switch
    {
        "active" or "subscribed" => PlusKind.Active,
        "renewing" => PlusKind.Renewing,
        "trial" or "intrial" or "trialing" or "freetrial" => PlusKind.Trial,
        "cancelled" or "canceled" or "pendingcancellation" => PlusKind.Cancelled,
        "expired" or "inactive" or "ended" or "terminated" => PlusKind.Ended,
        _ => PlusKind.Unknown,
    };

    // ─────────────── Transfer history ───────────────

    /// <summary>Id-like fields, in the order the ledger tries them as the stable per-transaction id.</summary>
    public static readonly string[] IdFields = ["idHash", "transactionId", "id", "purchaseToken"];

    public static TransferPage TransferPage(JsonElement r)
    {
        JsonElement data = r.ValueKind == JsonValueKind.Array ? r
            : FirstArray(r, "data", "transactions", "records") ?? throw new FormatException("histórico: lista de transações não encontrada");
        var items = new List<TransferRecord>();
        foreach (var t in data.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) throw new FormatException("histórico: item que não é objeto");
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in IdFields)
                if (t.TryGetProperty(f, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number && v.ToString() is { Length: > 0 } s && s != "0")
                    ids[f] = s;
            if (ids.Count == 0) throw new FormatException("histórico: transação sem campo de ID");

            DateTimeOffset created =
                (t.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.String ? ParseDate(c.GetString()!, "histórico: campo created com data ilegível") : (DateTimeOffset?)null)
                ?? MillisOf(t, "createdTimestampMs", "histórico")
                ?? (StringOf(t, "createdUtc", "createdAt", "timestamp") is { } alt ? ParseDate(alt, "histórico: data ilegível") : (DateTimeOffset?)null)
                ?? throw new FormatException("histórico: transação sem data");
            long amount = AmountOf(t) ?? throw new FormatException("histórico: transação sem valor");
            string? role = StringOf(t, "transferRole")
                           ?? (t.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? StringOf(d, "transferRole") : null);
            // A record we cannot classify could be an outgoing transfer: refusing it keeps usage from being undercounted.
            if (!string.Equals(role, "Sender", StringComparison.OrdinalIgnoreCase) && !string.Equals(role, "Receiver", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("histórico: transferRole ausente ou desconhecido");
            // sortOrder=Desc is what makes "older than 30 days" mean "done": verify it, never assume it.
            if (items.Count > 0 && created > items[^1].CreatedUtc + OrderTolerance)
                throw new FormatException("histórico: transações fora de ordem decrescente");
            items.Add(new TransferRecord(ids, created, amount, role!));
        }
        return new TransferPage(items, NextCursor(r));
    }

    /// <summary>Records listed a little out of order (same second, clock skew) are tolerated.</summary>
    internal static readonly TimeSpan OrderTolerance = TimeSpan.FromMinutes(1);

    private static readonly (string Field, string Param)[] CursorFields =
    [
        ("nextPageCursor", "cursor"),
        ("nextCursor", "cursor"),
        ("nextPageToken", "pageToken"),
    ];

    /// <summary>
    /// The next-page cursor, or null when the response says there is no next page. A forward cursor field
    /// we do not know ("next…Cursor/Token") is refused instead of being read as "last page".
    /// </summary>
    public static PageCursor? NextCursor(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var (field, param) in CursorFields)
            if (StringOf(r, field) is { Length: > 0 } value)
                return new PageCursor(param, value);
        foreach (var p in r.EnumerateObject())
        {
            string n = p.Name.ToLowerInvariant();
            bool forward = n.StartsWith("next") || n == "continuationtoken";
            bool cursorLike = n.Contains("cursor") || n.Contains("token") || n.Contains("page");
            bool known = CursorFields.Any(c => c.Field.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (forward && cursorLike && !known && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 })
                throw new FormatException($"paginação: campo {p.Name} desconhecido");
        }
        return null;
    }

    // ─────────────── helpers ───────────────

    private static long? AmountOf(JsonElement t)
    {
        long? root = OptionalLong(t, "amount");
        long? nested = t.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.Object ? OptionalLong(c, "amount") : null;
        if (root is { } a && nested is { } b && a != b)
            throw new FormatException("histórico: amount e currency.amount diferentes");
        return root ?? nested;
    }

    private static DateTimeOffset? MillisOf(JsonElement r, string name, string context)
    {
        if (OptionalLong(r, name) is not { } ms) return null;
        if (ms <= 0) throw new FormatException($"{context}: campo {name} inválido");
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    private static DateTimeOffset ParseDate(string s, string error) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? d : throw new FormatException(error);

    private static long RequireLong(JsonElement r, string name) =>
        OptionalLong(r, name) ?? throw new FormatException($"campo {name} ausente");

    private static long? OptionalLong(JsonElement r, string name)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out long l) => l,
            JsonValueKind.Number when v.TryGetDouble(out double d) && d == Math.Floor(d) => (long)d,
            JsonValueKind.String when long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long s) => s,
            JsonValueKind.Null => null,
            _ => throw new FormatException($"campo {name} com tipo inesperado"),
        };
    }

    private static string? StringOf(JsonElement r, params string[] names)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (r.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
        return null;
    }

    private static JsonElement? FirstArray(JsonElement r, params string[] names)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array) return v;
        return null;
    }

    private static JsonElement? FindProperty(JsonElement r, string name, int depth)
    {
        if (depth < 0) return null;
        if (r.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in r.EnumerateObject())
                if (p.NameEquals(name)) return p.Value;
            foreach (var p in r.EnumerateObject())
                if (FindProperty(p.Value, name, depth - 1) is { } found) return found;
        }
        else if (r.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in r.EnumerateArray())
                if (FindProperty(item, name, depth - 1) is { } found) return found;
        }
        return null;
    }
}
