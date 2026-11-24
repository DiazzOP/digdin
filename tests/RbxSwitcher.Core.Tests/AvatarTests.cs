using System.Net;
using System.Text;
using System.Text.Json;
using RbxSwitcher.Core.Avatars;
using RbxSwitcher.Core.Roblox;
using static RbxSwitcher.Core.Avatars.AvatarCategories;

namespace RbxSwitcher.Core.Tests;

/// <summary>Fictitious catalog and avatar responses; records every request and POST body.</summary>
internal sealed class FakeAvatarRoblox : HttpMessageHandler
{
    public readonly List<(string Method, string Url, string? Body)> Requests = [];
    public readonly List<string> WithCookie = [];
    public long UserId = 77;
    public HashSet<long> OwnedAssets = [];
    public HashSet<long> OwnedBundles = [];
    public List<object> Wearing = [new { id = 900L, name = "Old Hair", assetType = new { id = HairAccessory, name = "HairAccessory" } },
                                   new { id = 901L, name = "Old Shirt", assetType = new { id = Shirt, name = "Shirt" } },
                                   new { id = 902L, name = "Old Torso", assetType = new { id = Torso, name = "Torso" } },
                                   new { id = 903L, name = "Idle", assetType = new { id = 52, name = "IdleAnimation" } }];
    public bool ChallengePurchases;
    public int RateLimitAuthTimes;
    public HashSet<long> RefuseWearing = [];
    /// <summary>Asset types Roblox caps at 1 here: wearing two of one answers 400 LimitExceeded naming the second id.</summary>
    public Dictionary<long, int> TypeOf = [];
    public HashSet<int> CappedTypes = [];
    public object? Rules;
    public bool SearchV2Missing;
    /// <summary>Products the old economy endpoint refuses with InvalidArguments (items moved to the Marketplace).</summary>
    public HashSet<long> MigratedProducts = [];
    public object[] SearchItems = [];

    public IEnumerable<string> Posts(string pathPart) => Requests.Where(r => r.Method == "POST" && r.Url.Contains(pathPart)).Select(r => r.Body ?? "");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string? body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
        Requests.Add((req.Method.Method, req.RequestUri!.ToString(), body));
        if (req.Headers.Contains("Cookie")) WithCookie.Add(req.RequestUri!.AbsolutePath);
        var u = req.RequestUri!;
        string path = u.AbsolutePath;
        if (req.Method == HttpMethod.Post && !req.Headers.Contains("x-csrf-token"))
        {
            var r403 = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r403.Headers.Add("x-csrf-token", "CSRF1");
            return r403;
        }
        if (path == "/v1/users/authenticated")
        {
            if (RateLimitAuthTimes-- > 0) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return Json(new { id = UserId, name = "Fake", displayName = "Fake" });
        }
        if (path == "/v1/avatar") return Json(new { playerAvatarType = "R15", assets = Wearing });
        if (path == "/v1/avatar-rules") return Rules is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Rules);
        if (path.EndsWith("/is-owned"))
        {
            var parts = path.Split('/');
            long id = long.Parse(parts[^2]);
            bool owned = parts[^3] == "Bundle" ? OwnedBundles.Contains(id) : OwnedAssets.Contains(id);
            return Json(owned);
        }
        if (path == "/marketplace-items/v1/items/details")
            return Json(new[] { new { collectibleItemId = "cid-7", collectibleProductId = "prod-7", price = 0 } });
        if (path.StartsWith("/marketplace-sales/v1/item/"))
        {
            if (ChallengePurchases)
            {
                var ch = new HttpResponseMessage(HttpStatusCode.Forbidden);
                ch.Headers.Add("rblx-challenge-id", "x");
                return ch;
            }
            OwnedAssets.Add(7);
            return Json(new { purchased = true, purchaseResult = "Purchase transaction success.", errorMessage = (string?)null });
        }
        if (path.StartsWith("/v1/purchases/products/") && MigratedProducts.Contains(long.Parse(path.Split('/').Last())))
            return Json(new { purchased = false, reason = "InvalidArguments", productId = 0 });
        if (path.StartsWith("/v1/purchases/products/"))
        {
            if (ChallengePurchases)
            {
                var ch = new HttpResponseMessage(HttpStatusCode.Forbidden);
                ch.Headers.Add("rblx-challenge-id", "x");
                return ch;
            }
            long productId = long.Parse(path.Split('/').Last());
            OwnedAssets.Add(productId - 1_000_000); // fake: product id = asset id + 1 000 000
            OwnedBundles.Add(productId - 2_000_000);
            return Json(new { purchased = true, reason = "Success", productId });
        }
        if (path == "/v2/avatar/set-wearing-assets")
        {
            using var d = JsonDocument.Parse(body!);
            var ids = d.RootElement.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("id").GetInt64()).ToList();
            var over = ids.Where(id => TypeOf.ContainsKey(id) && CappedTypes.Contains(TypeOf[id])).GroupBy(id => TypeOf[id]).FirstOrDefault(g => g.Count() > 1);
            if (over is not null)
            {
                string inner = JsonSerializer.Serialize(new { ValidationErrors = new[] { new { Code = 5, Message = "LimitExceeded", FieldName = "AssetId", FieldData = over.Last().ToString() } } });
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { errors = new[] { new { code = 0, message = inner } } }), Encoding.UTF8, "application/json") };
            }
            var invalid = ids.Where(id => RefuseWearing.Contains(id)).ToArray();
            return Json(new { invalidAssets = Array.Empty<object>(), invalidAssetIds = invalid, success = true });
        }
        if (path is "/v2/avatar/set-body-colors" or "/v1/avatar/set-player-avatar-type") return Json(new { success = true });
        if (path.EndsWith("/search/items/details"))
        {
            if (SearchV2Missing && path.StartsWith("/v2/")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Json(new { keyword = (string?)null, previousPageCursor = (string?)null, nextPageCursor = (string?)null, data = SearchItems });
        }
        if (path == "/v1/bundles/details")
            return Json(new[] { new { id = 500L, name = "Fake Body", bundleType = "BodyParts",
                items = new object[] { new { id = 601L, name = "Torso", type = "Asset" }, new { id = 602L, name = "Head", type = "Asset" }, new { id = 9L, name = "Outfit", type = "UserOutfit" } },
                product = new { id = 2_000_500L, priceInRobux = (long?)null, isForSale = true, isFree = true }, creator = new { id = 1L, name = "Roblox", type = "User" } } });
        if (path == "/v1/catalog/items/details")
            return Json(new { data = new[] { new { id = 601L, itemType = "Asset", assetType = Torso }, new { id = 602L, itemType = "Asset", assetType = Head } } });
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
}

public class AvatarComposerTests
{
    private static AvatarItem Asset(long id, int type, bool free = true) => new(ItemKind.Asset, id, "item " + id, type, 0, id + 1_000_000, null, free);
    private static AvatarItem Body(long id, params (long Id, int Type)[] parts) =>
        new(ItemKind.Bundle, id, "body " + id, 0, BodyPartsBundle, id + 2_000_000, null, true, parts.ToDictionary(p => p.Id, p => p.Type));

    private static readonly CurrentAvatar Wearing = new([
        new(900, HairAccessory, "hair"), new(910, Hat, "hat"), new(901, Shirt, "shirt"), new(902, Torso, "torso"),
        new(905, Head, "head"), new(903, 52, "idle animation")], "R15");

    private static Dictionary<string, AvatarItem> Catalog(params AvatarItem[] items) => items.ToDictionary(i => i.Key);

    [Fact]
    public void Keep_empty_and_chosen_categories_build_the_full_list()
    {
        var hair = Asset(1, HairAccessory);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), hair);
        d.Slot(AvatarCategory.Accessories).Mode = SlotMode.Empty;

        var r = AvatarComposer.Compose(Wearing, d, Catalog(hair));
        Assert.Equal([901L, 902, 905, 903, 1], r.AssetIds); // old hair and hat gone; shirt, body, head, animation kept
        Assert.Empty(r.Skipped);
    }

    [Fact]
    public void A_body_bundle_replaces_body_parts_and_its_head_loses_to_a_chosen_head()
    {
        var body = Body(500, (601, Torso), (602, Head), (603, LeftArm));
        var head = Asset(700, DynamicHead);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Body), body);
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Head), head);

        var r = AvatarComposer.Compose(Wearing, d, Catalog(body, head));
        Assert.DoesNotContain(902L, r.AssetIds); // old torso
        Assert.DoesNotContain(905L, r.AssetIds); // old head (Head category replaced)
        Assert.DoesNotContain(602L, r.AssetIds); // bundle head loses to the chosen dynamic head
        Assert.Contains(601L, r.AssetIds);
        Assert.Contains(603L, r.AssetIds);
        Assert.Contains(700L, r.AssetIds);
        Assert.Contains(903L, r.AssetIds);       // animation untouched
    }

    [Fact]
    public void Two_bundles_with_eyebrows_wear_the_head_bundles_ones()
    {
        var body = Body(500, (601, Torso), (650, EyebrowAccessory), (651, EyelashAccessory));
        var head = new AvatarItem(ItemKind.Bundle, 700, "head 700", 0, DynamicHeadBundle, 2_000_700, null, true,
            new Dictionary<long, int> { [701] = DynamicHead, [750] = EyebrowAccessory, [751] = EyelashAccessory });
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Body), body);
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Head), head);

        var r = AvatarComposer.Compose(Wearing, d, Catalog(body, head));
        Assert.Contains(750L, r.AssetIds);
        Assert.Contains(751L, r.AssetIds);
        Assert.DoesNotContain(650L, r.AssetIds);
        Assert.DoesNotContain(651L, r.AssetIds);
        Assert.Contains(601L, r.AssetIds);
        Assert.Empty(r.Skipped); // pieces of a bundle give way without an error
    }

    [Fact]
    public void A_fourth_hat_goes_over_the_limit_and_is_reported()
    {
        var hats = Enumerable.Range(1, 4).Select(i => Asset(i, Hat)).ToArray();
        var d = new AvatarDesign();
        foreach (var h in hats) AvatarSelection.Toggle(d.Slot(AvatarCategory.Accessories), h);

        var r = AvatarComposer.Compose(Wearing, d, Catalog(hats));
        Assert.Equal(3, r.AssetIds.Count(id => id is >= 1 and <= 4));
        var (item, why) = Assert.Single(r.Skipped);
        Assert.Equal(1L, item.Id); // the first chosen gives way to the latest
        Assert.Equal(AvatarComposer.OverLimit, why);
    }

    [Fact]
    public void Roblox_rules_replace_the_defaults()
    {
        var rules = AvatarParsers.Rules(JsonDocument.Parse("""{"wearableAssetTypes":[{"maxNumber":1,"id":8,"name":"Hat"},{"maxNumber":2,"id":41,"name":"Hair"}]}""").RootElement);
        Assert.Equal((1, 2, 1, int.MaxValue), (rules.Max(Hat), rules.Max(HairAccessory), rules.Max(EyebrowAccessory), rules.Max(NeckAccessory)));
        Assert.Throws<FormatException>(() => AvatarParsers.Rules(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void The_refusal_in_the_screenshot_reads_as_a_reason()
    {
        const string body = """{"errors":[{"code":0,"message":"{\"ValidationErrors\":[{\"Code\":5,\"Message\":\"LimitExceeded\",\"FieldName\":\"AssetId\",\"FieldData\":\"123456789\"}]}"}]}""";
        var root = JsonDocument.Parse(body).RootElement;
        var errors = AvatarParsers.WearValidation(root);
        Assert.Equal([("LimitExceeded", (string?)"AssetId", (string?)"123456789")], errors);
        Assert.Contains("itens demais de um mesmo tipo", AvatarParsers.DescribeValidation(errors));
        string reason = RbxSwitcher.Core.Roblox.EconomyParsers.ErrorReason(root)!;
        Assert.DoesNotContain("{", reason);
        Assert.Contains("LimitExceeded", reason);

        var entries = new List<WearEntry> { new(900, EyebrowAccessory, 0, null), new(123456789, EyebrowAccessory, 1, null), new(5, Hat, 3, null) };
        Assert.Equal(900L, AvatarComposer.LimitCulprit(entries, errors)!.Id); // the kept one goes first
        Assert.Null(AvatarComposer.LimitCulprit([new(123456789, EyebrowAccessory, 3, null)], errors)); // nothing to choose between
    }

    [Fact]
    public void A_category_whose_items_cannot_be_worn_keeps_what_the_account_wears()
    {
        var paid = Asset(2, HairAccessory, free: false);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), paid);
        var r = AvatarComposer.Compose(Wearing, d, Catalog(paid), new HashSet<string> { paid.Key });
        Assert.Contains(900L, r.AssetIds);
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void Selection_keeps_one_per_slot_and_the_category_limit()
    {
        var slot = new SlotChoice();
        AvatarSelection.Toggle(slot, Asset(1, HairAccessory));
        AvatarSelection.Toggle(slot, Asset(2, HairAccessory));
        Assert.Equal([2L], slot.Items.Select(i => i.Id));            // hair: one at a time

        var head = new SlotChoice();
        AvatarSelection.Toggle(head, Asset(10, Head));
        AvatarSelection.Toggle(head, Asset(11, Face));
        AvatarSelection.Toggle(head, Asset(12, DynamicHead));      // replaces the classic head, keeps the face
        Assert.Equal([11L, 12], head.Items.Select(i => i.Id));

        var acc = new SlotChoice();
        foreach (long id in new long[] { 20, 21, 22, 23, 24 }) AvatarSelection.Toggle(acc, Asset(id, Hat));
        Assert.Equal(4, acc.Items.Count);                         // accessories: up to 4
        AvatarSelection.Toggle(acc, Asset(24, Hat));
        Assert.Equal(3, acc.Items.Count);                         // clicking again removes it
        foreach (var i in acc.Items.ToList()) AvatarSelection.Toggle(acc, Asset(i.Id, Hat));
        Assert.Equal(SlotMode.Keep, acc.Mode);                    // nothing chosen = keep the account's
    }

    [Fact]
    public void Randomize_respects_locks_and_is_repeatable_with_a_seed()
    {
        var pool = new[] { Asset(1, HairAccessory), Asset(2, HairAccessory), Asset(3, Hat), Asset(4, NeckAccessory), Asset(5, Shirt), Asset(6, Pants) };
        var d = new AvatarDesign { SkinHex = "694028", SkinLocked = true };
        d.Slot(AvatarCategory.Shirt).Locked = true;
        var a = AvatarRandomizer.Randomize(d, pool, new Random(7));
        var b = AvatarRandomizer.Randomize(d, pool, new Random(7));

        Assert.Equal(SlotMode.Keep, a.Slot(AvatarCategory.Shirt).Mode);         // locked
        Assert.Equal("694028", a.SkinHex);                                      // skin locked
        Assert.Single(a.Slot(AvatarCategory.Hair).Items);
        Assert.Equal(a.Slot(AvatarCategory.Pants).Items.Select(i => i.Id), b.Slot(AvatarCategory.Pants).Items.Select(i => i.Id));
        Assert.Equal(a.Slot(AvatarCategory.Hair).Items.Select(i => i.Id), b.Slot(AvatarCategory.Hair).Items.Select(i => i.Id));
        var accTypes = a.Slot(AvatarCategory.Accessories).Items.Select(i => i.AssetType).ToList();
        Assert.Equal(accTypes.Count, accTypes.Distinct().Count());
        Assert.Equal(SlotMode.Keep, a.Slot(AvatarCategory.Body).Mode);          // nothing in the pool: left alone
    }
}

public class AvatarParserTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{"id":1,"itemType":"Asset","assetType":41,"name":"Hair","price":0,"priceStatus":"Free","productId":5,"creatorTargetId":1,"creatorName":"Roblox"}""", true)]
    [InlineData("""{"id":1,"itemType":"Asset","assetType":41,"name":"Hair","price":0,"productId":5,"creatorTargetId":99,"creatorName":"Someone"}""", false)]   // not by Roblox
    [InlineData("""{"id":1,"itemType":"Asset","assetType":41,"name":"Hair","price":10,"productId":5,"creatorTargetId":1}""", false)]                          // costs Robux
    [InlineData("""{"id":1,"itemType":"Asset","assetType":41,"name":"Hair","price":0,"priceStatus":"Off Sale","productId":5,"creatorTargetId":1}""", false)] // off sale
    [InlineData("""{"id":1,"itemType":"Asset","assetType":41,"name":"Hair","price":0,"productId":5,"creatorTargetId":1,"itemRestrictions":["Limited"]}""", false)]
    [InlineData("""{"id":1,"itemType":"Asset","assetType":41,"name":"Hair","price":0,"collectibleItemId":"abc","creatorTargetId":1,"totalQuantity":500}""", false)] // limited stock
    [InlineData("""{"id":1,"itemType":"Asset","assetType":41,"name":"Hair","price":0,"creatorTargetId":1}""", false)]                                          // nothing to buy
    public void Free_means_by_Roblox_price_zero_on_sale_and_not_limited(string json, bool free) =>
        Assert.Equal(free, AvatarParsers.SearchItem(J(json))!.Free);

    [Fact]
    public void Items_the_builder_cannot_use_are_left_out_and_bundle_types_may_be_names()
    {
        Assert.Null(AvatarParsers.SearchItem(J("""{"id":1,"itemType":"Asset","assetType":61,"name":"Emote","price":0,"creatorTargetId":1,"productId":2}""")));
        var body = AvatarParsers.SearchItem(J("""{"id":3,"itemType":"Bundle","bundleType":1,"name":"Body","price":0,"creatorTargetId":1,"productId":4}"""));
        Assert.Equal(AvatarCategory.Body, body!.Category);
        var bundles = AvatarParsers.BundleDetails(J("""[{"id":3,"name":"Head","bundleType":"DynamicHead","items":[{"id":8,"type":"Asset"}],"product":{"id":4,"isFree":true,"isForSale":true},"creator":{"id":1}}]"""));
        Assert.Equal(DynamicHeadBundle, bundles[0].BundleType);
        Assert.True(bundles[0].Free);
    }

    [Fact]
    public void Current_avatar_needs_ids_and_types()
    {
        var a = AvatarParsers.Avatar(J("""{"playerAvatarType":"R6","assets":[{"id":5,"name":"x","assetType":{"id":8,"name":"Hat"}}]}"""));
        Assert.Equal((5L, 8), (a.Assets[0].Id, a.Assets[0].AssetType));
        Assert.Equal("R6", a.AvatarType);
        var e = Assert.Throws<FormatException>(() => AvatarParsers.Avatar(J("""{"assets":[{"id":5,"name":"segredo"}]}""")));
        Assert.DoesNotContain("segredo", e.Message);
    }

    [Theory]
    [InlineData("""{"purchased":true,"reason":"Success"}""", PurchaseOutcome.Acquired)]
    [InlineData("""{"purchased":false,"reason":"AlreadyOwned"}""", PurchaseOutcome.AlreadyOwned)]
    [InlineData("""{"purchased":false,"reason":"PriceChanged"}""", PurchaseOutcome.Refused)]
    [InlineData("""{"purchased":false,"errorMessage":"You already own this item."}""", PurchaseOutcome.AlreadyOwned)]
    public void Purchase_answers(string json, PurchaseOutcome expected) => Assert.Equal(expected, AvatarParsers.Purchase(J(json)).Outcome);
}

public class AvatarApplierTests
{
    private static AvatarItem Hair(long id, bool free = true) => new(ItemKind.Asset, id, "Hair " + id, HairAccessory, 0, id + 1_000_000, null, free);

    private static (FakeAvatarRoblox Fake, RobloxApi Api, AvatarApplier Applier) Make()
    {
        var fake = new FakeAvatarRoblox();
        var api = new RobloxApi(fake);
        return (fake, api, new AvatarApplier(api) { PurchaseGap = TimeSpan.Zero });
    }

    [Fact]
    public async Task Gets_the_free_item_for_zero_then_wears_it_with_the_skin_tone()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        var hair = Hair(1);
        var d = new AvatarDesign { SkinHex = "CC8E69" };
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), hair);

        var r = await applier.ApplyAsync(77, Fake.Session(), d, new Dictionary<string, AvatarItem> { [hair.Key] = hair }, acquireFree: true);

        Assert.Equal(ApplyStatus.Applied, r.Status);
        Assert.Equal(["Hair 1"], r.Acquired);
        var purchase = Assert.Single(fake.Posts("/purchases/products/1000001").Where(b => b.Length > 0).Distinct());
        Assert.Contains("\"expectedPrice\":0", purchase);
        Assert.Contains("\"headColor3\":\"CC8E69\"", fake.Posts("set-body-colors").Last());
        string wear = fake.Posts("set-wearing-assets").Last();
        Assert.Contains("{\"id\":1}", wear);
        Assert.DoesNotContain("900", wear);  // old hair off
        Assert.Contains("903", wear);        // animation kept
        Assert.All(fake.Requests, q => Assert.EndsWith(".roblox.com", new Uri(q.Url).Host));
    }

    [Fact]
    public async Task A_Marketplace_item_is_taken_through_marketplace_sales_for_zero()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        var hair = new AvatarItem(ItemKind.Asset, 7, "Curly Fade - Blonde", HairAccessory, 0, 1_000_007, "cid-7", true);
        fake.MigratedProducts.Add(1_000_007);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), hair);

        var r = await applier.ApplyAsync(77, Fake.Session(), d, new Dictionary<string, AvatarItem> { [hair.Key] = hair }, true);

        Assert.Equal(ApplyStatus.Applied, r.Status);
        Assert.Equal(["Curly Fade - Blonde"], r.Acquired);
        var buy = fake.Posts("/marketplace-sales/v1/item/cid-7/purchase-item").Last();
        Assert.Contains("\"expectedPrice\":0", buy);
        Assert.Contains("\"collectibleProductId\":\"prod-7\"", buy);
        Assert.Empty(fake.Posts("/v1/purchases/products/")); // the old endpoint is not needed
        Assert.Contains("{\"id\":7}", fake.Posts("set-wearing-assets").Last());
    }

    [Fact]
    public async Task When_both_endpoints_refuse_the_reason_is_readable()
    {
        var (fake, api, _) = Make();
        using var __ = api;
        fake.MigratedProducts.Add(1_000_009);
        var item = new AvatarItem(ItemKind.Asset, 9, "Striped T-Shirt", 2, 0, 1_000_009, null, true);
        var r = await api.AcquireFreeAsync(new SessionContext(Fake.Session()), 77, item);
        Assert.Equal(WriteOutcome.Refused, r.Outcome);
        Assert.Equal("o Roblox não entregou o item (InvalidArguments)", r.Problem);
    }

    [Fact]
    public async Task A_paid_item_is_never_bought_and_the_rest_still_applies()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        var paid = Hair(2, free: false);
        var d = new AvatarDesign { SkinHex = "FFCC99" };
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), paid);

        var r = await applier.ApplyAsync(77, Fake.Session(), d, new Dictionary<string, AvatarItem> { [paid.Key] = paid }, acquireFree: true);

        Assert.Equal(ApplyStatus.Partial, r.Status);
        Assert.Empty(fake.Posts("/purchases/"));
        Assert.Contains("900", fake.Posts("set-wearing-assets").Last()); // hair category kept as it was
        Assert.Contains(r.Skipped, s => s.Name == "Hair 2");
        var direct = await api.AcquireFreeAsync(new SessionContext(Fake.Session()), 77, paid);
        Assert.Equal(WriteOutcome.Refused, direct.Outcome);
        Assert.Empty(fake.Posts("/purchases/"));
    }

    [Fact]
    public async Task Another_accounts_cookie_changes_nothing()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        fake.UserId = 12345;
        var d = new AvatarDesign { SkinHex = "FFCC99" };
        var r = await applier.ApplyAsync(77, Fake.Session(), d, new Dictionary<string, AvatarItem>(), true);
        Assert.Equal(ApplyStatus.WrongAccount, r.Status);
        Assert.DoesNotContain(fake.Requests, q => q.Method == "POST");
    }

    [Fact]
    public async Task A_captcha_on_the_free_item_is_reported_and_not_bypassed()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        fake.ChallengePurchases = true;
        var h1 = Hair(1);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), h1);
        var r = await applier.ApplyAsync(77, Fake.Session(), d, new Dictionary<string, AvatarItem> { [h1.Key] = h1 }, true);
        Assert.Equal(ApplyStatus.Partial, r.Status);
        Assert.Contains("captcha", r.Skipped.Single().Why);
    }

    [Fact]
    public async Task Refused_assets_are_named_in_the_result()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        var h = Hair(3);
        fake.OwnedAssets.Add(3);
        fake.RefuseWearing.Add(3);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), h);
        var r = await applier.ApplyAsync(77, Fake.Session(), d, new Dictionary<string, AvatarItem> { [h.Key] = h }, true);
        Assert.Equal(ApplyStatus.Partial, r.Status);
        Assert.Empty(fake.Posts("/purchases/")); // already owned: not bought again
        Assert.Equal("o Roblox recusou vestir este item", r.Skipped.Single().Why);
    }

    [Fact]
    public async Task A_LimitExceeded_refusal_takes_the_weakest_piece_off_and_tries_again()
    {
        // The screenshot case: a body bundle and a head bundle both bring eyebrows; Roblox wears only one.
        var (fake, api, applier) = Make();
        using var _ = api;
        var body = new AvatarItem(ItemKind.Bundle, 500, "Base Body", 0, BodyPartsBundle, 2_000_500, null, true,
            new Dictionary<long, int> { [601] = Torso, [650] = 99 });
        fake.OwnedBundles.Add(500);
        fake.TypeOf[650] = 99; fake.TypeOf[1] = 99;
        fake.CappedTypes.Add(99); // a type the app does not know is limited
        var brow = new AvatarItem(ItemKind.Asset, 1, "Brows", 99, 0, 1_000_001, null, true);
        fake.OwnedAssets.Add(1);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Body), body);
        var catalog = new Dictionary<string, AvatarItem> { [body.Key] = body, [brow.Key] = brow };
        // The account already wears "Brows" (a type the builder has no category for, so it is kept); the bundle brings
        // another piece of the same type, and only Roblox knows that type is capped.
        fake.Wearing.Add(new { id = 1L, name = "Brows", assetType = new { id = 99, name = "Eyebrow" } });

        var r = await applier.ApplyAsync(77, Fake.Session(), d, catalog, true);

        Assert.Equal(ApplyStatus.Applied, r.Status);
        var wears = fake.Posts("set-wearing-assets").Distinct().ToList();
        Assert.Equal(2, wears.Count);                  // refused once, then sent again without one piece
        Assert.Contains("{\"id\":650}", wears[1]);    // the bundle's piece stays
        Assert.DoesNotContain("{\"id\":1}", wears[1]); // the kept one gives way
    }

    [Fact]
    public async Task Avatar_rules_from_Roblox_trim_before_sending()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        fake.Rules = new { wearableAssetTypes = new[] { new { maxNumber = 1, id = HairAccessory, name = "Hair Accessory" } } };
        fake.Wearing.Add(new { id = 905L, name = "Kept Hat", assetType = new { id = Hat, name = "Hat" } });
        var h1 = Hair(1);
        fake.OwnedAssets.Add(1);
        var d = new AvatarDesign();
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), h1);
        var r = await applier.ApplyAsync(77, Fake.Session(), d, new Dictionary<string, AvatarItem> { [h1.Key] = h1 }, true);
        Assert.Equal(ApplyStatus.Applied, r.Status);
        Assert.Single(fake.Requests, q => q.Url.EndsWith("/v1/avatar-rules"));
        Assert.DoesNotContain(fake.WithCookie, p => p.EndsWith("avatar-rules")); // public: no cookie
    }

    [Fact]
    public async Task The_batch_waits_on_429_and_retries_the_same_account()
    {
        var (fake, api, applier) = Make();
        using var _ = api;
        fake.RateLimitAuthTimes = 1;
        var waits = new List<TimeSpan>();
        var batch = new AvatarBatch(applier) { Gap = TimeSpan.Zero, Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        var d = new AvatarDesign { SkinHex = "FFCC99" };
        var results = new List<(string, ApplyStatus)>();
        await batch.RunAsync([new BatchTarget(77, "a", Fake.Session(), d), new BatchTarget(77, "b", Fake.Session(), d)],
            new Dictionary<string, AvatarItem>(), true, (t, r, _) => { if (r is not null) results.Add((t.Username, r.Status)); }, default);
        Assert.Equal([("a", ApplyStatus.Applied), ("b", ApplyStatus.Applied)], results);
        Assert.Contains(waits, w => w >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Free_catalog_falls_back_to_v1_and_fills_bundle_parts()
    {
        var fake = new FakeAvatarRoblox
        {
            SearchV2Missing = true,
            SearchItems = [
                new { id = 1L, itemType = "Asset", assetType = HairAccessory, name = "Free Hair", price = 0, priceStatus = "Free", productId = 1_000_001L, creatorTargetId = 1L, creatorName = "Roblox" },
                new { id = 500L, itemType = "Bundle", bundleType = 1, name = "Fake Body", price = 0, priceStatus = "Free", productId = 2_000_500L, creatorTargetId = 1L, creatorName = "Roblox" },
                new { id = 2L, itemType = "Asset", assetType = HairAccessory, name = "Paid Hair", price = 75, productId = 1_000_002L, creatorTargetId = 1L, creatorName = "Roblox" },
            ],
        };
        using var api = new RobloxApi(fake);
        var r = await api.GetFreeCatalogAsync(new SessionContext(Fake.Session()));
        Assert.True(r.Ok);
        Assert.Equal(["Fake Body", "Free Hair"], r.Value!.Select(i => i.Name).Order());
        var body = r.Value!.Single(i => i.Kind == ItemKind.Bundle);
        Assert.Equal(new Dictionary<long, int> { [601] = Torso, [602] = Head }, body.BundleAssets);
        Assert.Contains(fake.Requests, q => q.Url.Contains("/v1/search/items/details"));
        Assert.DoesNotContain(fake.WithCookie, p => p.Contains("search") || p.Contains("bundles/details")); // public reads: no session sent
    }
}

public class AvatarStorageTests
{
    [Fact]
    public void A_saved_design_and_the_catalog_cache_survive_json()
    {
        var hair = new AvatarItem(ItemKind.Asset, 1, "Hair", HairAccessory, 0, 1_000_001, null, true);
        var body = new AvatarItem(ItemKind.Bundle, 500, "Body", 0, BodyPartsBundle, 2_000_500, "abc", true, new Dictionary<long, int> { [601] = Torso });
        var d = new AvatarDesign { SkinHex = "CC8E69", AvatarType = "R6" };
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), hair);
        d.Slot(AvatarCategory.Accessories).Mode = SlotMode.Empty;
        d.Slot(AvatarCategory.Shirt).Locked = true;
        var preset = new AvatarPreset { Name = "Teste", SavedUtc = DateTimeOffset.UnixEpoch, Design = d };

        var back = JsonSerializer.Deserialize<AvatarPreset>(JsonSerializer.Serialize(preset))!;
        Assert.Equal("R6", back.Design.AvatarType);
        Assert.Equal(SlotMode.Empty, back.Design.Slot(AvatarCategory.Accessories).Mode);
        Assert.True(back.Design.Slot(AvatarCategory.Shirt).Locked);
        Assert.Equal(hair.Key, back.Design.Slot(AvatarCategory.Hair).Items.Single().Key);

        var items = JsonSerializer.Deserialize<List<AvatarItem>>(JsonSerializer.Serialize(new List<AvatarItem> { hair, body }))!;
        Assert.Equal(AvatarCategory.Body, items[1].Category);
        Assert.Equal(Torso, items[1].BundleAssets![601]);
        Assert.Equal("abc", items[1].CollectibleItemId);
    }
}

