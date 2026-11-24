using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Security;

namespace RbxSwitcher.App.UI;

/// <summary>
/// The "Plus" category of the active account: subscription status, validity or next renewal, the daily,
/// 30-day and per-transfer quotas, what the transfer history covered, and the read diagnostic.
/// </summary>
internal sealed class PlusView : Panel
{
    private readonly Label _title = new() { AutoSize = true, Font = Theme.Headline, ForeColor = Theme.Text, Margin = Padding.Empty };
    private readonly Label _account = Wrap(Theme.TextDim, font: Theme.Small);
    private readonly StatusText _status = new();
    private readonly Label _dates = Wrap(Theme.Text2), _plusRead = Wrap(Theme.TextDim, font: Theme.Fine);
    private readonly NoteLabel _plusNote = Note(), _quotaNote = Note(), _limitNote = Note();
    private readonly UiButton _ack;
    // Quotas: three figures side by side (caption, big number, detail), like the summary on Contas.
    private readonly TableLayoutPanel _quotaGrid = new() { AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 2, 0, 6), BackColor = Theme.Card };
    private readonly Label _daily = Figure(), _monthly = Figure(), _perTransfer = Figure();
    private readonly Label _dailySub = Wrap(Theme.Text2, font: Theme.Small), _monthlySub = Wrap(Theme.Text2, font: Theme.Small), _perTransferSub = Wrap(Theme.Text2, font: Theme.Small);
    private readonly Label _history = Wrap(Theme.TextDim, font: Theme.Small), _origin = Wrap(Theme.TextDim, font: Theme.Small);
    /// <summary>One plain line ("Histórico completo lido em 18/11 14:20"); the technical detail is collapsed below.</summary>
    private readonly Label _historySimple = Wrap(Theme.Text2, font: Theme.Small);
    private readonly Label _empty = Wrap(Theme.TextDim);
    private readonly TextBox _diagnostic = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Visible = false,
        BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8.25f),
        Height = 220,
    };
    private readonly UiButton _refresh, _fullHistory, _diagnose, _copy, _record;
    private readonly FlowLayoutPanel _body = Stack(Theme.Sidebar);
    private readonly CardStack[] _cards;
    private readonly FlowLayoutPanel _tools = Stack(Theme.Card);
    private bool _canQuery;
    private int _secondsLeft;

    public event Action? RefreshRequested;
    public event Action? FullHistoryRequested;
    public event Action? DiagnosticRequested;
    public event Action? RecordRequested;
    public event Action? LimitDropAcknowledged;
    public event Action? LimitedsRefreshRequested;
    public event Action? TargetRequested;
    private readonly UiButton _target;

    // ── Limiteds and space (distribution) ──
    private readonly Label _role = Wrap(Theme.Text2, font: Theme.Small);
    private readonly TableLayoutPanel _spaceGrid = new() { AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 2, 0, 6), BackColor = Theme.Card };
    private readonly Label _spaceNow = Figure(), _spaceAtSale = Figure(), _occupied = Figure();
    private readonly Label _spaceNowSub = Wrap(Theme.Text2, font: Theme.Small), _spaceAtSaleSub = Wrap(Theme.Text2, font: Theme.Small), _occupiedSub = Wrap(Theme.Text2, font: Theme.Small);
    private readonly NoteLabel _spaceNote = Note();
    private readonly Label _copies = Wrap(Theme.Text, font: Theme.Small), _invRead = Wrap(Theme.TextDim, font: Theme.Fine);
    private readonly UiButton _readLimiteds;

    public PlusView()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Theme.DarkScroll(this);
        BackColor = Theme.Sidebar;
        _title.Text = "Roblox Plus";
        _account.Margin = new Padding(0, 2, 0, 12);

        _refresh = Theme.Button("Atualizar dados", (_, _) => RefreshRequested?.Invoke(), primary: true);
        _refresh.Margin = new Padding(0, 8, 0, 0);
        _fullHistory = Theme.Button("Reler histórico completo (30 dias)", (_, _) => FullHistoryRequested?.Invoke());
        _fullHistory.Margin = new Padding(0, 6, 0, 0);
        _diagnose = Theme.Button("Diagnóstico da leitura do Plus", (_, _) => DiagnosticRequested?.Invoke());
        _copy = Theme.Button("Copiar diagnóstico", (_, _) => { if (_diagnostic.Text.Length > 0) Clipboard.SetText(_diagnostic.Text); });
        _record = Theme.Button("Gravar página do Plus (60 s)", (_, _) => RecordRequested?.Invoke());
        _copy.Visible = false;

        // "Detalhes técnicos": how the figures were read, the history read and the diagnostic tools — collapsed.
        var showTools = new UiButton { Text = "Detalhes técnicos", Kind = ButtonKind.Ghost, Icon = UiIcon.ChevronDown, IconAfterText = true, Margin = new Padding(0, 6, 0, 0) };
        showTools.Click += (_, _) =>
        {
            _tools.Visible = !_tools.Visible;
            showTools.Icon = _tools.Visible ? UiIcon.ChevronUp : UiIcon.ChevronDown;
            showTools.Invalidate();
        };
        _tools.Visible = false;
        _tools.Controls.AddRange([
            Caption("Como os números foram lidos"), _origin,
            Caption("Última leitura do histórico"), _history,
            Caption("Diagnóstico da assinatura"),
            Wrap(Theme.TextDim, font: Theme.Small, text: "Consulta as assinaturas com a sessão desta conta e mostra só status HTTP, nomes e tipos de campos e onde a leitura falhou. Sem cookies, valores ou dados de pagamento."),
            _diagnose,
            Wrap(Theme.TextDim, font: Theme.Small, text: "Ao iniciar a gravação, o app volta à aba Contas. Abra Roblox Plus no menu do site para registrar as chamadas da página durante 60 s."),
            _record, _diagnostic, _copy,
        ]);
        _ack = Theme.Button("Ok, entendi", (_, _) => LimitDropAcknowledged?.Invoke(), ButtonKind.Ghost);
        _ack.Margin = new Padding(0, 0, 0, 6);
        _ack.Visible = false;
        _readLimiteds = Theme.Button("Ler Limiteds agora", (_, _) => LimitedsRefreshRequested?.Invoke());
        _readLimiteds.Margin = new Padding(0, 8, 6, 0);
        _target = Theme.Button("Meta desta conta…", (_, _) => TargetRequested?.Invoke(), ButtonKind.Ghost);
        _target.Margin = new Padding(0, 8, 0, 0);
        var limitedButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = Padding.Empty };
        limitedButtons.Controls.AddRange([_readLimiteds, _target]);
        AddFigure(_spaceGrid, 0, "Espaço agora", _spaceNow, _spaceNowSub);
        AddFigure(_spaceGrid, 1, "Na venda", _spaceAtSale, _spaceAtSaleSub);
        AddFigure(_spaceGrid, 2, "Ocupado", _occupied, _occupiedSub);
        AddFigure(0, "Hoje", _daily, _dailySub);
        AddFigure(1, "30 dias", _monthly, _monthlySub);
        AddFigure(2, "Por transferência", _perTransfer, _perTransferSub);
        _cards = [
            Card("Assinatura", _status, _dates, _plusNote, _plusRead, _refresh),
            Card("Cotas de transferência", _limitNote, _ack, _quotaGrid, _quotaNote, _origin),
            Card("Histórico de 30 dias", _historySimple, _fullHistory, showTools, _tools),
            Card("Limiteds e espaço", _role, _spaceNote, _spaceGrid, _copies, _invRead, limitedButtons),
        ];
        _body.Controls.AddRange([_title, _account, _empty, .._cards]);
        Controls.Add(_body);
        ResizeContent();
    }

    private static FlowLayoutPanel Stack(Color color) => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, BackColor = color, Margin = Padding.Empty,
    };

    /// <summary>Bloxkeep card: card fill, hairline, 14 px radius; title in mixed case, no icon.</summary>
    private static CardStack Card(string title, params Control[] controls)
    {
        var card = new CardStack();
        card.Controls.Add(new Label
        {
            Text = title, AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text,
            Margin = new Padding(0, 0, 0, 8),
        });
        card.Controls.AddRange(controls);
        return card;
    }

    private static NoteLabel Note() => new() { Visible = false };

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Font = Theme.Header, ForeColor = Theme.Text2, Margin = new Padding(0, 10, 0, 2) };

    private static Label Figure() => new() { AutoSize = true, Font = Theme.Headline, ForeColor = Theme.Text, Margin = new Padding(0, 1, 0, 1) };

    private void AddFigure(int column, string caption, Label value, Label sub) => AddFigure(_quotaGrid, column, caption, value, sub);

    private static void AddFigure(TableLayoutPanel grid, int column, string caption, Label value, Label sub)
    {
        var _quotaGrid = grid;
        _quotaGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        var cell = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 12, 0), BackColor = Theme.Card };
        cell.Controls.AddRange([Wrap(Theme.TextDim, caption, Theme.Small), value, sub]);
        _quotaGrid.Controls.Add(cell, column, 0);
    }

    private static void SetNote(NoteLabel note, string text, NoteKind kind = NoteKind.Warn)
    {
        note.Kind = kind;
        note.Text = text;
        note.Visible = text.Length > 0;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (_cards is not null) ResizeContent();
    }

    private void ResizeContent()
    {
        int gutter = LogicalToDeviceUnits(12);
        int available = ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        int width = Math.Max(1, Math.Min(LogicalToDeviceUnits(920), available - 2 * gutter));
        int inset = Math.Max(gutter, (available - width) / 2);
        _body.Padding = new Padding(inset, gutter, gutter, gutter);
        _empty.MaximumSize = new Size(width, 0);
        foreach (var card in _cards)
        {
            card.MinimumSize = card.MaximumSize = new Size(width, 0);
            int inner = Math.Max(1, width - card.Padding.Horizontal);
            foreach (var label in card.Controls.OfType<Label>())
                label.MaximumSize = new Size(inner, 0);
        }
        int inner0 = Math.Max(1, width - _cards[1].Padding.Horizontal);
        _quotaGrid.MinimumSize = _quotaGrid.MaximumSize = _spaceGrid.MinimumSize = _spaceGrid.MaximumSize = new Size(inner0, 0);
        int column = Math.Max(1, inner0 / 3 - LogicalToDeviceUnits(12));
        foreach (var label in new[] { _dailySub, _monthlySub, _perTransferSub, _spaceNowSub, _spaceAtSaleSub, _occupiedSub }) label.MaximumSize = new Size(column, 0);
        int toolsWidth = Math.Max(1, width - _cards[^1].Padding.Horizontal);
        foreach (var label in _tools.Controls.OfType<Label>())
            label.MaximumSize = new Size(toolsWidth, 0);
        _diagnostic.Width = Math.Max(1, toolsWidth - _diagnostic.Margin.Horizontal);
    }

    private static Label Wrap(Color color, string text = "", Font? font = null) => new()
    {
        AutoSize = true, ForeColor = color, Font = font ?? Theme.Base, Text = text, Margin = new Padding(0, 2, 0, 3),
    };

    public void ShowBusy(bool refreshing, bool diagnosing, bool canQuery)
    {
        _canQuery = canQuery;
        _refresh.Enabled = _fullHistory.Enabled = canQuery && !refreshing;
        _refresh.Text = refreshing ? "Atualizando…" : "Atualizar dados";
        _diagnose.Enabled = canQuery && !diagnosing;
        _record.Enabled = canQuery && _secondsLeft <= 0;
        _diagnose.Text = diagnosing ? "Consultando…" : "Diagnóstico da leitura do Plus";
    }

    public void ShowRecording(int secondsLeft)
    {
        _secondsLeft = secondsLeft;
        _record.Enabled = _canQuery && secondsLeft <= 0;
        _record.Text = secondsLeft > 0 ? $"Gravando… {secondsLeft} s" : "Gravar página do Plus (60 s)";
    }

    public void ShowNoAccount()
    {
        _empty.Text = "Abra uma conta na categoria Contas para ver a assinatura e as cotas.";
        _empty.Visible = true;
        _account.Visible = false;
        foreach (var card in _cards) card.Visible = false;
    }

    public void Show(string username, AccountFinance? f, DateTimeOffset now)
    {
        _empty.Visible = false;
        _title.Text = "@" + username;
        _account.Text = "Assinatura do Roblox Plus e cotas de transferência";
        _account.Visible = true;
        foreach (var card in _cards) card.Visible = true;

        var plus = PlusStatus.Evaluate(f, now);
        _status.Set(Fmt.PlusText(plus), Fmt.PlusColor(plus), Fmt.PlusInk(plus));
        _dates.Text = Fmt.PlusDates(plus);
        _dates.Visible = _dates.Text.Length > 0;
        string note = plus switch
        {
            { Reason: { } why } => "A conferir: " + Redact.Text(why),
            { Expiring: true } => "Faltam até 7 dias para vencer e o Roblox não confirmou a renovação.",
            { Label: PlusLabel.Trial } => "Período de teste confirmado pelo Roblox.",
            _ => "",
        };
        // Details Roblox did not report usably: shown, never blocking the status.
        if (plus.Reason is null && plus.Info?.Notes is { Count: > 0 } notes)
            note = (note.Length > 0 ? note + "\n" : "") + "Observação: " + string.Join("; ", notes) + ".";
        bool infoOnly = plus.Reason is null && !plus.Expiring;
        SetNote(_plusNote, note, infoOnly ? NoteKind.Info : NoteKind.Warn);
        // A failed attempt never looks like a successful read: both times are shown separately.
        bool failedNow = f?.FailedReads.Contains(FinanceField.Plus) == true;
        _plusRead.Text = (failedNow && f!.LastAttemptUtc is { } la ? $"última tentativa {Fmt.Time(la)} (recusada)" : "")
                         + (failedNow && plus.ReadUtc is not null ? " · " : "")
                         + (plus.ReadUtc is { } at ? (failedNow ? "última leitura válida " : "lido do Roblox em ") + Fmt.Time(at) : "");

        if (f is null)
        {
            _daily.Text = _monthly.Text = _perTransfer.Text = "—";
            _daily.ForeColor = _monthly.ForeColor = _perTransfer.ForeColor = Theme.TextDim;
            _dailySub.Text = _monthlySub.Text = _perTransferSub.Text = "";
            SetNote(_quotaNote, "");
            SetNote(_limitNote, "");
            _ack.Visible = false;
            _origin.Text = "";
            _history.Text = "";
            _historySimple.Text = "Dados ainda não lidos. Use \"Atualizar dados\".";
            return;
        }
        var v = QuotaCalculator.Compute(f, now);
        SetNote(_limitNote, Fmt.LimitDrops(LimitWatch.Pending(f)));
        _ack.Visible = _limitNote.Visible;
        SetFigure(_daily, v.AvailableToday);
        // One fact per line: short columns wrap badly mid-phrase.
        _dailySub.Text = string.Join("\n", new[]
        {
            v.EffectiveDaily is { } ed ? $"de {Fmt.N(ed)}" : "",
            v.TodayLimitedByMonth ? "limitado pelos 30 dias" : "",
            $"renova {BrasiliaTime.ToBrasilia(v.NextDailyResetUtc):HH:mm}",
        }.Where(x => x.Length > 0));
        SetFigure(_monthly, v.RemainingMonthly);
        _monthlySub.Text = string.Join("\n", new[]
        {
            v.EffectiveMonthly is { } em ? $"de {Fmt.N(em)}" : "",
            v.Sent30Days is { } s30 ? $"enviado {Fmt.N(s30)}" : "",
            v.MonthlyReleases.FirstOrDefault(r => r.AtUtc > now) is { } next ? $"+{Fmt.N(next.Amount)} em {BrasiliaTime.ToBrasilia(next.AtUtc):dd/MM}" : "",
        }.Where(x => x.Length > 0));
        SetFigure(_perTransfer, v.PerTransferLimit is { } pt && !v.FailedReads.Contains(FinanceField.Caps) ? pt : null);
        _perTransferSub.Text = "máximo por envio";
        SetNote(_quotaNote, v.State == CapacityState.ToVerify && v.ToVerifyReasons.Count > 0
            ? "\"Pode enviar agora\" está a conferir porque: " + string.Join("; ", v.ToVerifyReasons) + "." : "");
        var origin = new List<string>();
        if (v.CapDaily is { } cd) origin.Add($"teto diário do Roblox {Fmt.N(cd)}");
        if (v.CapMonthly is { } cm) origin.Add($"teto mensal do Roblox {Fmt.N(cm)}");
        if (v.RestrictionDaily is { } rdl) origin.Add($"sua restrição diária {Fmt.N(rdl)}");
        if (v.RestrictionMonthly is { } rml) origin.Add($"sua restrição mensal {Fmt.N(rml)}");
        _origin.Text = (origin.Count > 0 ? string.Join(" · ", origin) + ". " : "") + "Restantes são estimativas do painel; o dia vira à meia-noite de Brasília e cada envio sai da janela 30 dias depois.";
        _historySimple.Text = f.LastHistoryRead is { } hs
            ? $"Histórico {(hs.FullRead ? "completo" : "atualizado")} lido em {Fmt.Time(hs.AtUtc)} · {Fmt.Count(hs.Outgoing, "envio", "envios")} nos últimos 30 dias."
            : "Histórico ainda não lido.";

        _history.Text = f.LastHistoryRead is { } h
            ? $"{(h.FullRead ? "Leitura completa" : "Leitura incremental")} em {Fmt.Time(h.AtUtc)}: {h.Pages} página(s), {h.Records} registro(s), " +
              $"{h.Outgoing} saída(s), {h.Incoming} entrada(s)" + (h.Duplicates > 0 ? $", {h.Duplicates} repetido(s) ignorado(s)" : "") +
              (h.IdField is { } idf ? $", ID: {idf}" : "") +
              (h.OldestUtc is { } o && h.NewestUtc is { } n ? $", de {Fmt.Time(o)} a {Fmt.Time(n)}" : "") + $" — {h.Stop}."
            : "Histórico ainda não lido.";
    }

    /// <summary>The account's Limiteds and its space for new lots (see <see cref="LimitedsDetail"/>).</summary>
    public void ShowLimiteds(LimitedsDetail d)
    {
        _role.Text = d.Role;
        _readLimiteds.Enabled = d.CanRead;
        _readLimiteds.Text = d.Reading ? "Lendo Limiteds…" : "Ler Limiteds agora";
        var v = d.Space;
        _spaceGrid.Visible = v is not null; // sources and accounts outside the distribution have no "space"
        _target.Visible = v is not null;
        SetFigure(_spaceNow, v?.SpaceNow);
        SetFigure(_spaceAtSale, v?.SpaceAtSale);
        SetFigure(_occupied, v?.Occupied);
        _spaceNowSub.Text = v?.Ceiling is { } c ? $"teto {Fmt.N(c)}" : "";
        _spaceAtSaleSub.Text = v?.CeilingAtSale is { } ca
            ? $"em {Fmt.Count(d.DaysToSale, "dia", "dias")}: teto {Fmt.N(ca)} (a janela de 30 dias libera {Fmt.N(v.ReleasedBySale ?? 0)})"
            : $"em {Fmt.Count(d.DaysToSale, "dia", "dias")}, com a cota que a janela de 30 dias libera";
        _occupiedSub.Text = v?.StockNet is { } sn
            ? $"saldo {Fmt.N(v.Balance ?? 0)} + pendentes {Fmt.N(v.Pending ?? 0)} + estoque {Fmt.N(sn)}" + (v.Committed > 0 ? $" + a caminho {Fmt.N(v.Committed)}" : "")
            : "saldo + pendentes + estoque";
        SetNote(_spaceNote, v is { Blocked: true } ? "Sem novos lotes por enquanto: " + string.Join("; ", v.Blocks) + "." : "");
        _copies.Text = d.CopiesText;
        _copies.Visible = d.CopiesText.Length > 0;
        _invRead.Text = d.ReadText;
    }

    /// <summary>A number in text ink, or "a conferir" in warn ink (never a guessed value).</summary>
    private static void SetFigure(Label label, long? value)
    {
        label.Text = value is { } n ? Fmt.N(n) : Fmt.ToVerify;
        label.ForeColor = value is null ? Theme.WarnInk : Theme.Text;
    }

    public void ShowDiagnostic(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        if (_diagnostic.Text != normalized) _diagnostic.Text = normalized;
        _diagnostic.Visible = _copy.Visible = true;
    }

    public void ClearDiagnostic()
    {
        _diagnostic.Text = "";
        _diagnostic.Visible = _copy.Visible = false;
    }
}
