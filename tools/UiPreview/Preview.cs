using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RbxSwitcher.App.Browser;
using RbxSwitcher.App.Platform;
using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Avatars;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Roblox;
using RbxSwitcher.Core.Security;
using RbxSwitcher.Core.Storage;

namespace RbxSwitcher.App;

/// <summary>Opens the real main window with fictitious accounts and saves screenshots of each screen. Design preview only.</summary>
internal static class Preview
{
    private static string _out = "";
    private static string _tag = "";

    [STAThread]
    private static void Main(string[] args)
    {
        _out = args.Length > 0 ? args[0] : "shots";
        _tag = args.Length > 1 ? args[1] : "a";
        Directory.CreateDirectory(_out);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string dataDir = Path.Combine(Path.GetTempPath(), "rbxpreview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        Core.Logging.Log.Init(Path.Combine(dataDir, "app.log"));
        var protector = new PlainProtector();
        var vault = VaultFile.Create(Path.Combine(dataDir, "vault.bin"), protector);
        Seed(vault).GetAwaiter().GetResult();
        var form = new MainForm(vault, protector, dataDir) { StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen!.Bounds };
        form.Shown += async (_, _) =>
        {
            try { await Script(form, vault); }
            catch (Exception e) { File.WriteAllText(Path.Combine(_out, _tag + "-error.txt"), e.ToString()); }
            Environment.Exit(0);
        };
        Application.Run(form);
    }

    // ───────────── fictitious data ─────────────

    private static readonly (string User, string Display, string? Group, long Robux)[] People =
    [
        ("FonteAlfa", "Alfa", "Principal", 12_480), ("altconta72", "Kuba", "Principal", 3_150), ("altconta35", "altconta", "Principal", 48_900),
        ("LuaNoite_07", "Lua", "Alts", 0), ("PedroGamer2011", "Pedro Gamer", "Alts", 845), ("mari_sky", "Mari", "Alts", 22_015),
        ("ThiagoBlox", "Thiago", "Alts", 1_200), ("ZeroCool_BR", "Zero", null, 75), ("anaclara_rbx", "Ana Clara", null, 9_999),
        ("FastRunner88", "Fast Runner", null, 310), ("NinjaDoSul", "Ninja do Sul", "Alts", 5_400), ("MegaBuilderPro", "Mega Builder", "Principal", 130_250),
    ];

    private static async Task Seed(VaultFile vault)
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < People.Length; i++)
        {
            var p = People[i];
            long id = 1_000_000 + i * 7919;
            string session = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" + new string('X', 200) + i;
            vault.Accounts.Upsert(id, p.User, p.Display, session);
            var a = vault.Accounts.Find(id)!;
            a.Group = p.Group;
            var fake = new FakeRoblox { UserId = id, Robux = p.Robux, Pending = i * 37 % 500 };
            fake.Pages[""] = (i % 3 == 0
                ? [FakeRoblox.Tx("a" + i, now.AddHours(-2), -1_200, "Sender"), FakeRoblox.Tx("b" + i, now.AddDays(-12), -3_800, "Sender")]
                : i % 3 == 1 ? [FakeRoblox.Tx("c" + i, now.AddDays(-3), -500, "Sender")] : [], null);
            if (i is 3 or 7) fake.Subscriptions = new { subscriptions = Array.Empty<object>() };
            using var api = new RobloxApi(fake);
            var r = await new FinanceRefresher(api).RefreshAsync(id, session, null);
            var f = r.Finance;
            f.LastAttemptUtc = now; // keep the app's background refresh from trying the network
            switch (i)
            {
                case 1: f.Plus = new PlusInfo { Active = true, Trial = true, ExpiresUtc = now.AddDays(20), WillRenew = true, RenewsUtc = now.AddDays(20) }; break;
                case 2: f.Plus = new PlusInfo { Active = true, ExpiresUtc = now.AddDays(23), WillRenew = true, RenewsUtc = now.AddDays(23) }; break;
                case 4: f.Plus = new PlusInfo { Active = true, ExpiresUtc = now.AddDays(4), WillRenew = null }; break;
                case 5: f.Plus = new PlusInfo { Active = false, Status = "Expired", ExpiresUtc = now.AddDays(-9) }; break;
                case 6: f.FailedReads.Add(FinanceField.Plus); f.FailureReasons[FinanceField.Plus] = "assinaturas: campo nextRenewalTimestampMs inválido"; break;
                case 7: f.Plus!.TrialEligible = true; break;
                case 9:
                    f.Caps = new TransferCaps { Daily = 500, Monthly = 1000, PerTransfer = 500 };
                    f.LimitDrops.Add(new LimitDrop { Kind = LimitKind.Daily, Source = LimitSource.Roblox, From = 5000, To = 500, AtUtc = now.AddMinutes(-40) });
                    f.LimitDrops.Add(new LimitDrop { Kind = LimitKind.Monthly, Source = LimitSource.Roblox, From = 10000, To = 1000, AtUtc = now.AddMinutes(-40) });
                    break;
                case 10: f.RobuxAtUtc = now.AddHours(-3); break; // stale balance
            }
            a.Finance = f;
        }
        // Limited distribution: FonteAlfa and MegaBuilderPro are the sources; the "Alts" group receives the lots.
        var accs = vault.Accounts.All;
        vault.Settings.Distribution = new RbxSwitcher.Core.Limiteds.DistributionSettings
        {
            SourceAccounts = [accs[0].UserId, accs[11].UserId], DestinationGroup = "Alts", SmallRapLimit = 800, DefaultTargetNet = 9_000,
        };
        long ua = 900_000;
        RbxSwitcher.Core.Limiteds.LimitedCopy C(long asset, string name, long rap, bool hold = false) => new(ua++, asset, name, (int)(ua % 900), rap, hold);
        accs[0].Inventory = new RbxSwitcher.Core.Limiteds.AccountInventory
        {
            Complete = true, ReadUtc = now, LastAttemptUtc = now,
            Copies = [C(1, "Valkyrie Helm", 3_000), C(2, "Sparkle Time Fedora", 2_500, true), C(3, "Dominus Frigidus", 5_400), C(4, "Epic Egg", 300), C(5, "Deteggctive W. Wolf", 450)],
        };
        foreach (int i in new[] { 3, 4, 5, 6, 10 })
            accs[i].Inventory = new RbxSwitcher.Core.Limiteds.AccountInventory
            {
                Complete = true, ReadUtc = now, LastAttemptUtc = now,
                Copies = i == 5 ? [C(1, "Valkyrie Helm", 3_000), C(4, "Epic Egg", 300)] : [C(5, "Deteggctive W. Wolf", 450)],
            };
        vault.Avatars.Add(new AvatarPreset { Name = "Ruivo casual", SavedUtc = now.AddDays(-2), Design = new AvatarDesign { SkinHex = "EAB892" } });
        vault.Avatars.Add(new AvatarPreset { Name = "Alt padrão", SavedUtc = now.AddDays(-6), Design = new AvatarDesign { SkinHex = "694028", AvatarType = "R15" } });
    }

    // ───────────── the walk-through ─────────────

    private static async Task Script(MainForm form, VaultFile vault)
    {
        await Task.Delay(6000);
        var accounts = vault.Accounts.All;
        foreach (var a in accounts) a.Status = SessionStatus.Valid;
        accounts[8].Status = SessionStatus.Expired;
        accounts[11].Status = SessionStatus.Unreachable;
        var browser = (BrowserHost)Field(form, "_browser");
        typeof(BrowserHost).GetProperty("Active")!.SetValue(browser, new SessionTab(accounts[0].UserId));
        Call(form, "RefreshList");
        Call(form, "UpdateToolbar");
        await Task.Delay(1500);
        Shot(form, "1-contas");

        Call(form, "ShowView", 1);
        await Task.Delay(1500);
        Shot(form, "2-plus");
        var grid = (DataGridView)Field((PlusAccountsView)Field(form, "_plusPage"), "_list");
        typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", Any)!.Invoke(grid,
            [new DataGridViewCellMouseEventArgs(3, -1, 5, 5, new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0))]);
        await Task.Delay(1000);
        Shot(form, "2b-plus-ordenado-cota-hoje");
        SetField(form, "_market", new Dictionary<long, RbxSwitcher.Core.Limiteds.ItemMarket>
        {
            [1] = new(1, "Valkyrie Helm", 3_000, 3_200, 3, 4.2, DateTimeOffset.UtcNow), [2] = new(2, "Sparkle Time Fedora", 2_500, 2_800, 2, 0.6, DateTimeOffset.UtcNow),
            [3] = new(3, "Dominus Frigidus", 5_400, null, 1, 0.3, DateTimeOffset.UtcNow), [4] = new(4, "Epic Egg", 300, null, null, null, DateTimeOffset.UtcNow),
            [5] = new(5, "Deteggctive W. Wolf", 450, null, null, null, DateTimeOffset.UtcNow),
        });
        SetField(form, "_rolimonsAt", (DateTimeOffset?)DateTimeOffset.UtcNow);
        Call(form, "UpdateFinancePanel");
        typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", Any)!.Invoke(grid,
            [new DataGridViewCellMouseEventArgs(5, -1, 5, 5, new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0))]);
        await Task.Delay(1000);
        Shot(form, "2c-plus-espaco");
        ((PlusAccountsView)Field(form, "_plusPage")).OpenAccount(accounts[5].UserId);
        await Task.Delay(1200);
        var pv = (PlusView)Field(form, "_plusView");
        pv.AutoScrollPosition = new Point(0, 2000);
        await Task.Delay(600);
        Shot(form, "2d-detalhe-destinataria");
        ((PlusAccountsView)Field(form, "_plusPage")).OpenAccount(accounts[0].UserId);
        await Task.Delay(1200);
        pv.AutoScrollPosition = new Point(0, 2000);
        await Task.Delay(600);
        Shot(form, "2e-detalhe-origem");
        await ModalShot(() => Call(form, "EditDistribution"), "2f-config-distribuicao", null);

        // ── Distribuir: generate, then fake one opened step, one completed (with a sale) and one needing a check ──
        var itemThumbs = (Dictionary<string, Image>)Field(form, "_thumbs");
        foreach (var (id, name) in new[] { (1L, "Valkyrie Helm"), (2L, "Sparkle Time Fedora"), (3L, "Dominus Frigidus"), (4L, "Epic Egg"), (5L, "Deteggctive W. Wolf"), (6L, "Hat") })
            itemThumbs["a" + id] = Thumb(new AvatarItem(ItemKind.Asset, id, name, 8, 0, null, null, true));
        Call(form, "ShowView", 3);
        await Task.Delay(800);
        Call(form, "GeneratePlan", true);
        await Task.Delay(800);
        Shot(form, "8a-distribuir-sugestoes");
        var plan = vault.Plan;
        var completed = new RbxSwitcher.Core.Limiteds.PlanStep
        {
            Id = plan.NewId(), SourceId = accounts[0].UserId, SourceName = "FonteAlfa", DestinationId = accounts[4].UserId, DestinationName = "PedroGamer2011",
            Limiteds = [new(990001, 1, "Valkyrie Helm", 11, 2_100)], DestinationSmall = new(990002, 5, "Deteggctive W. Wolf", 3),
            State = RbxSwitcher.Core.Limiteds.StepState.Completed, TradeId = 6_123_456_789, CreatedUtc = DateTimeOffset.UtcNow.AddDays(-4), CompletedUtc = DateTimeOffset.UtcNow.AddDays(-4),
            ActualFromSource = [new(990001, 1, "Valkyrie Helm", 11, 2_100)], ActualFromDestination = [new(990003, 4, "Epic Egg", 7)],
        };
        completed.Log(DateTimeOffset.UtcNow.AddDays(-4), "conferida e aberta no Roblox pelo RbxSwitcher");
        completed.Log(DateTimeOffset.UtcNow.AddDays(-4), "trade 6123456789 concluída e conferida · Small de @PedroGamer2011 trocado: o plano previa Deteggctive W. Wolf #3, a trade levou Epic Egg #7");
        var check = new RbxSwitcher.Core.Limiteds.PlanStep
        {
            Id = plan.NewId(), SourceId = accounts[11].UserId, SourceName = "MegaBuilderPro", DestinationId = accounts[6].UserId, DestinationName = "ThiagoBlox",
            Limiteds = [new(990010, 3, "Dominus Frigidus", 4, 3_780)], DestinationSmall = new(990011, 5, "Deteggctive W. Wolf", 9),
            State = RbxSwitcher.Core.Limiteds.StepState.NeedsCheck, CreatedUtc = DateTimeOffset.UtcNow.AddHours(-3),
            Attention = "As cópias já estão em @ThiagoBlox: a trade pode ter sido feita fora do RbxSwitcher. Localize a trade para conferir.",
        };
        check.Log(DateTimeOffset.UtcNow.AddHours(-1), "em conferência: as cópias já estão em @ThiagoBlox");
        plan.Steps.Add(completed);
        plan.Steps.Add(check);
        if (plan.Steps.FirstOrDefault(x => x.State == RbxSwitcher.Core.Limiteds.StepState.Suggested) is { } first)
        {
            first.State = RbxSwitcher.Core.Limiteds.StepState.Opened;
            first.OpenedUtc = DateTimeOffset.UtcNow;
            first.Log(DateTimeOffset.UtcNow, "conferida e aberta no Roblox pelo RbxSwitcher");
        }
        accounts[4].Sales = new RbxSwitcher.Core.Limiteds.AccountSales
        {
            ReadUtc = DateTimeOffset.UtcNow,
            Records = [new("h1", DateTimeOffset.UtcNow.AddDays(-1), 1, "Valkyrie Helm", 2_240, false), new("h2", DateTimeOffset.UtcNow.AddHours(-5), 77, "Outro item", 90, false)],
        };
        var dview = (DistributeView)Field(form, "_distributePage");
        Call(form, "UpdateDistribute", true);
        await Task.Delay(600);
        Shot(form, "8b-distribuir-sugestoes-depois");
        var dtabs = (Segmented)Field(dview, "_tabs");
        foreach (var (i, name) in new[] { (1, "8c-em-andamento"), (2, "8d-ficaram-de-fora"), (3, "8e-historico"), (4, "8f-vendas") })
        {
            dtabs.SelectedIndex = i;
            await Task.Delay(700);
            Shot(form, name);
        }
        dtabs.SelectedIndex = 3;
        ((HashSet<string>)Field(dview, "_detailsOpen")).Add(completed.Id);
        Call(dview, "Render");
        await Task.Delay(700);
        Shot(form, "8g-historico-detalhes");

        // ── Limiteds: copies on sale (one outbid), ready, on hold, a sale; cheapest listings per item ──
        foreach (var (id, name) in new[] { (7L, "Black Headphones"), (8L, "Pink Eye") })
            itemThumbs["a" + id] = Thumb(new AvatarItem(ItemKind.Asset, id, name, 8, 0, null, null, true));
        var market = (Dictionary<long, RbxSwitcher.Core.Limiteds.ItemMarket>)Field(form, "_market");
        var mnow = DateTimeOffset.UtcNow;
        market[7] = new(7, "Black Headphones", 3_130, 3_200, 3, 2, mnow);
        market[8] = new(8, "Pink Eye", 2_060, null, 2, 1, mnow);
        market[1] = new(1, "Valkyrie Helm", 3_000, null, 3, 4.2, mnow);
        market[3] = new(3, "Dominus Frigidus", 5_400, 5_600, 2, 0.3, mnow);
        market[2] = new(2, "Sparkle Time Fedora", 12_500, null, 2, 0.6, mnow);
        var sigma = accounts[4];
        sigma.Inventory!.Copies.AddRange([new(910_001, 7, "Black Headphones", 12, 3_130, false), new(910_002, 8, "Pink Eye", 3, 2_060, false)]);
        accounts[3].Inventory!.Copies.Add(new(910_003, 3, "Dominus Frigidus", 4, 5_400, false));
        accounts[10].Inventory!.Copies.Add(new(910_004, 1, "Valkyrie Helm", 11, 3_000, true));
        var sl = new RbxSwitcher.Core.Limiteds.AccountListings();
        sl.Apply(7, [new RbxSwitcher.Core.Limiteds.OwnCopy(910_001, 12, 3_200)], sigma.Inventory.Copies, mnow);
        sl.Apply(8, [new RbxSwitcher.Core.Limiteds.OwnCopy(910_002, 3, 2_050)], sigma.Inventory.Copies, mnow);
        sl.Apply(5, [], sigma.Inventory.Copies, mnow);
        sigma.Listings = sl;
        var l3 = new RbxSwitcher.Core.Limiteds.AccountListings();
        l3.Apply(3, [], accounts[3].Inventory!.Copies, mnow);
        accounts[3].Listings = l3;
        var resellers = vault.Resellers;
        resellers[7] = new(7, [new(5, 3_050, null, null, null), new(6, 3_090, null, null, null), new(7, 3_150, null, null, null), new(sigma.UserId, 3_200, 910_001, null, 12)], mnow);
        resellers[8] = new(8, [new(sigma.UserId, 2_050, 910_002, null, 3), new(8, 2_090, null, null, null)], mnow);
        resellers[3] = new(3, [new(9, 5_290, null, null, null), new(10, 5_400, null, null, null)], mnow);
        resellers[1] = new(1, [new(11, 2_990, null, null, null)], mnow);
        SetField(form, "_sellAt", (DateTimeOffset?)mnow);
        Call(form, "ShowView", 4);
        await Task.Delay(1500);
        Shot(form, "9a-limiteds");
        var lview = (LimitedsView)Field(form, "_sellPage");
        ((Segmented)Field(lview, "_mode")).SelectedIndex = 1;
        await Task.Delay(900);
        Shot(form, "9b-limiteds-por-item");
        ((Segmented)Field(lview, "_mode")).SelectedIndex = 0;
        Click(((Chip[])Field(lview, "_chips"))[1]);
        await Task.Delay(900);
        Shot(form, "9c-limiteds-superadas");
        // Em holding: Valkyrie Helm received in a trade accepted in the app 20 h ago; the Fedora bought on the store 2 days ago.
        accounts[10].Arrivals = new() { Accepted = { [5_550_001] = mnow.AddHours(-20) },
            Items = [new("t5550001:0", RbxSwitcher.Core.Limiteds.ArrivalKind.Trade, 1, 910_004, null, "Valkyrie Helm", mnow.AddHours(-21), false, 5_550_001)] };
        accounts[0].Arrivals = new() { Items = [new("p1", RbxSwitcher.Core.Limiteds.ArrivalKind.Purchase, 2, null, null, "Sparkle Time Fedora", mnow.AddDays(-2).AddHours(-3), true)] };
        Click(((Chip[])Field(lview, "_chips"))[4]);
        Call(form, "UpdateSelling");
        await Task.Delay(1200);
        Shot(form, "9d-limiteds-holding");
        var sellGrid = (DataGridView)Field(lview, "_list");
        for (int r = sellGrid.Rows.Count - 1; r >= 0; r--)
            if (sellGrid.Rows[r].Cells[0].Value?.ToString()?.Contains("Fedora") == true || r == sellGrid.Rows.Count - 1) { sellGrid.CurrentCell = sellGrid.Rows[r].Cells[0]; break; }
        await Task.Delay(900);
        Shot(form, "9e-limiteds-holding-compra");
        Click(((Chip[])Field(lview, "_chips"))[0]);

        // ── Calendário: pending sales of three accounts and sales already seen falling (≈ 5 days) ──
        var cnow = DateTimeOffset.UtcNow;
        RbxSwitcher.Core.Limiteds.SaleRecord Sale(string id, double daysAgo, long asset, string name, long amount, bool pending) => new(id, cnow.AddDays(-daysAgo), asset, name, amount, pending);
        RbxSwitcher.Core.Limiteds.PendingRelease Rel(string id, double daysAgo, double hold, long amount, string name) =>
            new(id, cnow.AddDays(-daysAgo), cnow.AddDays(-daysAgo + hold).AddMinutes(-25), cnow.AddDays(-daysAgo + hold), amount, 1, name);
        accounts[0].Sales = new() { ReadUtc = cnow, LastAttemptUtc = cnow,
            Records = [Sale("c1", 1.2, 7, "Black Headphones", 2_191, true), Sale("c2", 3.5, 1, "Valkyrie Helm", 2_093, true), Sale("c3", 9, 8, "Pink Eye", 1_442, false)],
            Releases = [Rel("r1", 12, 5.1, 1_442, "Pink Eye"), Rel("r2", 9, 4.9, 980, "Epic Egg"), Rel("r3", 7, 5.2, 2_240, "Valkyrie Helm")] };
        accounts[4].Sales = new() { ReadUtc = cnow, LastAttemptUtc = cnow,
            Records = [Sale("p1", 0.4, 3, "Dominus Frigidus", 3_780, true), Sale("p2", 5.6, 8, "Pink Eye", 1_120, true)] };
        accounts[11].Sales = new() { ReadUtc = cnow, LastAttemptUtc = cnow, Records = [Sale("m1", 2.1, 1, "Valkyrie Helm", 2_050, true),
            Sale("m2", 2.0, 2, "Sparkle Time Fedora of the Midnight Winter Collection", 8_750, true)] };
        foreach (var (i, pending) in new[] { (0, 4_584L), (4, 4_900L), (11, 2_050L) }) if (accounts[i].Finance is { } fin) fin.PendingRobux = pending;
        Call(form, "ShowView", 5);
        await Task.Delay(1500);
        Shot(form, "10a-calendario");
        var cview = (CalendarView)Field(form, "_calendarPage");
        Call(cview, "Choose", RbxSwitcher.Core.Limiteds.RobuxCalendar.Day(cnow.AddDays(-4)));
        await Task.Delay(900);
        Shot(form, "10b-calendario-queda-vista");
        Call(cview, "Choose", RbxSwitcher.Core.Limiteds.RobuxCalendar.Day(cnow.AddDays(3)));
        Call(cview, "ToggleAccount", accounts[11].UserId);
        await Task.Delay(900);
        Shot(form, "10c-calendario-uma-conta");
        Call(cview, "ToggleAccount", accounts[11].UserId);
        ((Control)Field(cview, "_grid")).Focus();
        ((Button)Field(cview, "_how")).PerformClick();
        await Task.Delay(900);
        Shot(form, "10d-calendario-foco-explicacao");
        ((Button)Field(cview, "_how")).PerformClick();
        cview.SetState(RbxSwitcher.Core.Limiteds.RobuxCalendar.Build([], cnow), false, true);
        await Task.Delay(900);
        Shot(form, "10e-calendario-sem-leitura");
        Call(form, "ShowView", 0);
        var plusPage = (PlusAccountsView)Field(form, "_plusPage");
        plusPage.OpenAccount(accounts[4].UserId);
        await Task.Delay(1500);
        Shot(form, "3-plus-detalhe-expirando");
        plusPage.OpenAccount(accounts[9].UserId);
        await Task.Delay(1000);
        Shot(form, "3b-plus-detalhe-limite");

        // Avatares: first as it is without network (catalog fails), then with a fictitious catalog.
        SetField(form, "_ownedItems", new Dictionary<long, List<AvatarItem>> { [accounts[0].UserId] = Owned() }, replaceDictionary: true);
        Call(form, "ShowView", 2);
        for (int i = 0; i < 90 && (bool)Field(form, "_catalogLoading"); i++) await Task.Delay(1000);
        await Task.Delay(1000);
        Shot(form, "4a-avatares-sem-catalogo");

        var catalog = Catalog();
        SetField(form, "_freeCatalog", catalog);
        SetField(form, "_catalogAt", (DateTimeOffset?)DateTimeOffset.UtcNow);
        SetField(form, "_catalogProblem", null);
        var thumbs = (Dictionary<string, Image>)Field(form, "_thumbs");
        foreach (var it in catalog.Concat(Owned())) thumbs[it.Key] = Thumb(it);
        Call(form, "ShowCatalogStatus");
        var view = (AvatarsView)Field(form, "_avatarsPage");
        var d = view.Design;
        AvatarSelection.Toggle(d.Slot(AvatarCategory.Hair), catalog.First(c => c.Category == AvatarCategory.Hair));
        foreach (var acc in catalog.Where(c => c.Category == AvatarCategory.Accessories).Take(2)) AvatarSelection.Toggle(d.Slot(AvatarCategory.Accessories), acc);
        d.Slot(AvatarCategory.TShirt).Mode = SlotMode.Empty;
        d.Slot(AvatarCategory.Body).Locked = true;
        d.SkinHex = "CC8E69";
        Call(view, "RefreshState");
        await Task.Delay(1500);
        Shot(form, "4b-avatares");

        // Batch dialog (modal): a timer takes the picture and closes it.
        await ModalShot(() => Call(form, "ApplyAvatarMany", d.Clone()), "5-lote", null);

        // Player dialog, closed and with "Mais opções" open.
        var link = PlayerLinks.Classify("roblox://navigation/currency_transfer?recipientUserId=12345");
        var handler = new ProtocolHandlers.Handler(true, "RobloxPlayerBeta.exe", @"%LocalAppData%\Roblox\Versions\version-4ea8e85481e34f46", true);
        await ModalShot(() => PlayerLaunchDialog.Ask(form, "FonteAlfa", link, handler), "6-abrir-roblox", null);
        await ModalShot(() => PlayerLaunchDialog.Ask(form, "FonteAlfa", link, handler), "7-abrir-roblox-opcoes",
            f => { foreach (var b in All(f).OfType<Button>().Where(b => b.Text.StartsWith("Mais opções") || b.Text.StartsWith("Detalhes"))) b.PerformClick(); });
    }

    private static async Task ModalShot(Action open, string name, Action<Form>? before)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            var f = Form.ActiveForm ?? Application.OpenForms.Cast<Form>().LastOrDefault();
            if (f is null) return;
            before?.Invoke(f);
            await Task.Delay(800);
            Shot(f, name);
            f.Close();
        };
        timer.Start();
        open();
        await Task.Delay(500);
    }

    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));

    /// <summary>Asks the outside script (which can read the X screen) to take the picture, and waits for it.</summary>
    private static void Shot(Form f, string name)
    {
        f.Refresh();
        Application.DoEvents();
        Thread.Sleep(300);
        Application.DoEvents();
        string ready = Path.Combine(_out, "ready-" + name), done = Path.Combine(_out, "done-" + name);
        File.WriteAllText(ready, name);
        for (int i = 0; i < 200 && !File.Exists(done); i++) { Thread.Sleep(100); Application.DoEvents(); }
    }

    private static List<AvatarItem> Catalog()
    {
        var list = new List<AvatarItem>();
        string[] hair = ["Wavy Middle Part - Brown", "Pony Tail - Blonde", "Top Knot - Black", "Colorful Braids", "Straight Bangs - Black", "Curly Afro - Black",
                         "Short Fringe - Black", "Surfer - Red", "Side Part - Brown", "Braided Hair - Blonde", "Short Curls - Red", "Medium Middle Part - Red"];
        long id = 100;
        foreach (var n in hair) list.Add(new AvatarItem(ItemKind.Asset, id++, n, AvatarCategories.HairAccessory, 0, id + 1_000_000, null, true));
        foreach (var n in new[] { "Roblox Visor", "Black Sunglasses", "Silver Necklace", "Backpack" })
            list.Add(new AvatarItem(ItemKind.Asset, id++, n, n.Contains("Neck") ? 43 : n.Contains("Back") ? 46 : n.Contains("Sun") ? 42 : 8, 0, id + 1_000_000, null, true));
        foreach (var n in new[] { "Classic Shirt - Blue", "Hoodie - Gray", "Jacket - Black" }) list.Add(new AvatarItem(ItemKind.Asset, id++, n, 11, 0, id + 1_000_000, null, true));
        foreach (var n in new[] { "Jeans - Blue", "Cargo Pants" }) list.Add(new AvatarItem(ItemKind.Asset, id++, n, 12, 0, id + 1_000_000, null, true));
        list.Add(new AvatarItem(ItemKind.Asset, id++, "Smile", 18, 0, id + 1_000_000, null, true));
        list.Add(new AvatarItem(ItemKind.Bundle, id++, "Robloxian 2.0", 0, 1, id + 2_000_000, null, true, new Dictionary<long, int> { [9001] = 27, [9002] = 17 }));
        return list;
    }

    private static List<AvatarItem> Owned() =>
        [new AvatarItem(ItemKind.Asset, 5000, "Valkyrie Helm", 8, 0, null, null, false), new AvatarItem(ItemKind.Asset, 5001, "Brown Charmer Hair", 41, 0, null, null, false)];

    private static Image Thumb(AvatarItem it)
    {
        var bmp = new Bitmap(96, 96);
        using var g = Graphics.FromImage(bmp);
        int h = Math.Abs(it.Name.GetHashCode());
        g.Clear(Color.FromArgb(32, 36, 46));
        using var b = new SolidBrush(Color.FromArgb(120 + h % 100, 70 + h / 7 % 80, 40 + h / 13 % 60));
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillEllipse(b, 22, 14, 52, 56);
        g.FillRectangle(b, 26, 50, 44, 30);
        return bmp;
    }

    // ───────────── reflection helpers (preview only) ─────────────

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static void Click(Control c) =>
        typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(c, [EventArgs.Empty]);

    private static object Field(object o, string name) => o.GetType().GetField(name, Any)!.GetValue(o)!;

    private static void SetField(object o, string name, object? value, bool replaceDictionary = false)
    {
        var f = o.GetType().GetField(name, Any)!;
        if (replaceDictionary && f.GetValue(o) is System.Collections.IDictionary target && value is System.Collections.IDictionary src)
        {
            foreach (System.Collections.DictionaryEntry e in src) target[e.Key] = e.Value;
            return;
        }
        f.SetValue(o, value);
    }

    private static object? Call(object o, string name, params object[] args)
    {
        var m = o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length);
        return m.Invoke(o, args);
    }

    private sealed class PlainProtector : IKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] blob) => blob.ToArray();
    }
}

/// <summary>Fictitious Roblox responses (same shapes as the unit tests).</summary>
internal sealed class FakeRoblox : HttpMessageHandler
{
    public long UserId = 77, Robux = 1_693, Pending = 120;
    public object Subscriptions = new { subscriptions = new[] { new { productType = "RobloxPlus", status = "Active", expiration = DateTimeOffset.UtcNow.AddDays(30).ToString("O") } } };
    public object Caps = new { dailyLimit = 5000, monthlyLimit = 10000, perTransferLimit = 2000 };
    public Dictionary<string, (object[]? Items, string? Next)> Pages = new() { [""] = ([], null) };

    public static object Tx(string id, DateTimeOffset at, long amount, string role) =>
        new { id, created = at.ToString("O"), currency = new { amount, type = "Robux" }, transferRole = role };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string path = req.RequestUri!.AbsolutePath;
        object? body =
            path == "/v1/users/authenticated" ? new { id = UserId, name = "u" + UserId, displayName = "U" } :
            path == "/v1/user/currency" ? new { robux = Robux } :
            path.EndsWith("/transaction-totals") ? new { pendingRobuxTotal = Pending, salesTotal = 0 } :
            path == "/subscriptions/v2/user/subscriptions" ? Subscriptions :
            path == "/transfer/v1/robux-transfer/user-transfer-limit" ? Caps :
            path.StartsWith("/user-settings-api/") ? new { robuxTransferLimits = new { currentValue = (object?)null } } :
            path.Contains("/transaction-records/v1/users/") ? new { previousPageCursor = (string?)null, nextPageCursor = Pages[""].Next, data = Pages[""].Items } :
            null;
        return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
    }
}

internal static class Program
{
    public static string VaultPath(string dataDir) => Path.Combine(dataDir, "vault.bin");
}
