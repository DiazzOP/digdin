using RbxSwitcher.App.Platform;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.App.UI;

/// <summary>
/// "Distribuição de Limiteds": source accounts (where AstraFlip withdrawals land), which accounts receive lots, the
/// marketplace fee, the net ceiling, the RAP limit for Smalls, the Small reserve and the days until a sale pays.
/// Edits a copy; the caller saves it only on "Salvar".
/// </summary>
internal sealed class DistributionDialog : Form
{
    private const string AllOthers = "Todas as outras contas";
    private readonly DistributionSettings _s;
    private readonly Dictionary<long, Chip> _sources = new();
    private readonly Dictionary<long, Chip> _dests = new();
    private readonly Dictionary<long, string?> _groupOf = new();
    private readonly DropdownButton _dest = new("Recebem os lotes");
    private readonly FieldBox _fee = new("30"), _target = new("0"), _small = new("1000"), _reserve = new("1"), _days = new("7"), _undercut = new("1"), _floor = new("80");
    private readonly Chip _rolimons = new("Usar o valor da Rolimons quando houver") { Outline = true };
    private readonly Chip _atSale = new("Planejar pelo espaço na venda") { Outline = true };
    private readonly Chip _preselect = new("Pré-selecionar os itens ao abrir a trade") { Outline = true };
    private readonly Chip _openDest = new("Depois de enviar, abrir a destinatária para aceitar") { Outline = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Theme.BadInk, Font = Theme.Small, Margin = new Padding(0, 6, 0, 0) };

    public DistributionSettings Result => _s;

    public DistributionDialog(DistributionSettings current, IReadOnlyList<Account> accounts, IEnumerable<string> groups)
    {
        _s = System.Text.Json.JsonSerializer.Deserialize<DistributionSettings>(System.Text.Json.JsonSerializer.Serialize(current))!;
        Text = "Distribuição de Limiteds";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual; // placed in Load, after sizing to the screen
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        int w = LogicalToDeviceUnits(500);

        // Padding on the content (an auto-sized form ignored its own right padding and cut text at the edge).
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(20, 16, 24, 16) };
        Label L(string t, Font? f = null, Color? c = null, int top = 0) =>
            new() { Text = t, AutoSize = true, MaximumSize = new Size(w - LogicalToDeviceUnits(16), 0), Font = f ?? Theme.Small, ForeColor = c ?? Theme.TextDim, Margin = new Padding(0, top, 0, 4) };

        body.Controls.Add(L("Distribuição de Limiteds", Theme.Headline, Theme.Text));
        body.Controls.Add(L("Os saques da AstraFlip entram só nas contas de origem: todo Limited que não for Small nelas é tratado como vindo da AstraFlip."));

        body.Controls.Add(L("Contas de origem", Theme.Header, Theme.Text2, 10));
        var sources = new FlowLayoutPanel { AutoSize = false, WrapContents = true, Width = w, Height = LogicalToDeviceUnits(96), AutoScroll = true, Margin = new Padding(0, 0, 0, 4) };
        Theme.DarkScroll(sources);
        foreach (var a in accounts.Where(a => a.HasSession).OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase))
        {
            var chip = new Chip("@" + a.Username) { Outline = true, On = _s.SourceAccounts.Contains(a.UserId), Margin = new Padding(0, 0, 4, 4) };
            chip.Click += (_, _) => chip.On = !chip.On;
            _sources[a.UserId] = chip;
            sources.Controls.Add(chip);
        }
        body.Controls.Add(sources);

        body.Controls.Add(L("Destinatárias", Theme.Header, Theme.Text2, 8));
        body.Controls.Add(L("Escolha um grupo para marcar as contas dele e desmarque (ou marque) contas uma a uma. Só as contas marcadas recebem lotes."));
        _dest.SetItems([AllOthers, .. groups]);
        _dest.Selected = _s.DestinationGroup ?? AllOthers;
        _dest.Margin = new Padding(0, 0, 0, 6);
        body.Controls.Add(_dest);
        var dests = new FlowLayoutPanel { AutoSize = false, WrapContents = true, Width = w, Height = LogicalToDeviceUnits(128), AutoScroll = true, Margin = new Padding(0, 0, 0, 4) };
        Theme.DarkScroll(dests);
        foreach (var a in accounts.Where(a => a.HasSession).OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase))
        {
            _groupOf[a.UserId] = a.GroupOrDefault;
            var chip = new Chip("@" + a.Username) { Outline = true, Margin = new Padding(0, 0, 4, 4) };
            chip.Click += (_, _) => chip.On = !chip.On;
            _dests[a.UserId] = chip;
            dests.Controls.Add(chip);
        }
        // Marked: the chosen accounts, or (none chosen) the group's accounts.
        foreach (var (id, chip) in _dests) chip.On = _s.DestinationAccounts.Count > 0 ? _s.DestinationAccounts.Contains(id) : InGroup(id);
        _dest.SelectionChanged += () => { foreach (var (id, chip) in _dests) chip.On = InGroup(id); };
        body.Controls.Add(dests);
        body.Controls.Add(L("Uma conta de origem nunca recebe lotes, mesmo marcada aqui. Para pausar uma conta, desmarque-a.", top: 2));

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(300)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(120)));
        void Row(string label, string help, FieldBox box, long value)
        {
            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 12, 10) };
            text.Controls.Add(L(label, Theme.Base, Theme.Text));
            text.Controls.Add(L(help));
            ((Label)text.Controls[1]).MaximumSize = new Size(LogicalToDeviceUnits(290), 0);
            box.Box.Text = value.ToString();
            box.Width = LogicalToDeviceUnits(110);
            box.Margin = new Padding(0, 4, 0, 10);
            box.Box.AccessibleName = label;
            grid.Controls.Add(text);
            grid.Controls.Add(box);
        }
        Row("Taxa do Marketplace (%)", "Sai de cada venda. Líquido = preço × (1 − taxa), arredondado para baixo.", _fee, _s.FeePercent);
        Row("Meta líquida por conta", "Teto de Robux líquidos parados em cada destinatária. 0 = sem teto (vale só a cota de 30 dias).", _target, _s.DefaultTargetNet);
        Row("Limite de RAP para Small", "Cópias com RAP até este valor são Smalls (itens de apoio nas trades).", _small, _s.SmallRapLimit);
        Row("Reserva mínima de Smalls", "Smalls que cada conta guarda para as próprias trades.", _reserve, _s.MinSmallsReserve);
        Row("Dias até a venda", "Do envio até o Robux da venda chegar (holding + tempo para vender). Usado no espaço \"na venda\".", _days, _s.DaysToSale);
        Row("Venda: abaixo do menor preço", "Aba Limiteds: a sugestão fica tantos Robux abaixo do menor preço de outro vendedor.", _undercut, _s.SellUndercut);
        Row("Venda: piso da sugestão (% do RAP)", "A sugestão nunca fica abaixo desta parte do RAP. 0 = sem piso.", _floor, _s.SellFloorPercent);
        body.Controls.Add(grid);

        _rolimons.On = _s.PreferRolimonsValue;
        _rolimons.Click += (_, _) => _rolimons.On = !_rolimons.On;
        body.Controls.Add(_rolimons);
        body.Controls.Add(L("Sem isso, o preço estimado é o RAP. Preço manual por item: aba Distribuir → Ficaram de fora → Preço….", top: 2));
        _atSale.On = _s.UseSpaceAtSale;
        _atSale.Margin = new Padding(0, 8, 0, 0);
        _atSale.Click += (_, _) => _atSale.On = !_atSale.On;
        body.Controls.Add(_atSale);
        body.Controls.Add(L("Ligado: conta a cota que a janela de 30 dias libera até o Robux da venda chegar. Desligado: só o espaço de agora (mais conservador).", top: 2));
        _preselect.On = _s.PreselectTradeItems;
        _preselect.Margin = new Padding(0, 8, 0, 0);
        _preselect.Click += (_, _) => _preselect.On = !_preselect.On;
        body.Controls.Add(_preselect);
        body.Controls.Add(L("\"Abrir trade\" marca os Limiteds e os Smalls da etapa na página de trade. O app nunca envia: você confere e envia no Roblox.", top: 2));
        _openDest.On = _s.OpenDestinationAfterSend;
        _openDest.Margin = new Padding(0, 8, 0, 0);
        _openDest.Click += (_, _) => _openDest.On = !_openDest.On;
        body.Controls.Add(_openDest);
        body.Controls.Add(L("Quando você envia a trade da etapa e ela confere, o app passa para a conta destinatária e abre a trade em Trocas. Aceitar é com você; se a trade não bater com a etapa, o app não troca de conta.", top: 2));
        body.Controls.Add(_error);

        // The settings scroll; Salvar/Cancelar stay at the bottom and the window never grows past the screen.
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(20, 10, 20, 14), BackColor = Theme.Sidebar };
        var save = Theme.Button("Salvar", (_, _) => Save(), primary: true);
        var cancel = Theme.Button("Cancelar", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        save.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.AddRange([save, cancel]);
        buttons.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, buttons.Width, 0); };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Sidebar };
        Theme.DarkScroll(scroll);
        scroll.Controls.Add(body);
        Controls.Add(scroll);
        Controls.Add(buttons);
        Load += (_, _) =>
        {
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            var content = body.GetPreferredSize(Size.Empty);
            int width = content.Width + SystemInformation.VerticalScrollBarWidth + LogicalToDeviceUnits(8);
            int height = Math.Min(content.Height + buttons.GetPreferredSize(Size.Empty).Height, area.Height - LogicalToDeviceUnits(60));
            ClientSize = new Size(width, height);
            var center = Owner is { } o ? new Point(o.Left + o.Width / 2, o.Top + o.Height / 2) : new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
            Location = new Point(Math.Clamp(center.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
                                 Math.Clamp(center.Y - Height / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        };
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void Save()
    {
        bool Num(FieldBox f, long min, long max, string name, out long v)
        {
            if (long.TryParse(f.Box.Text.Trim().Replace(".", ""), out v) && v >= min && v <= max) return true;
            _error.Text = $"{name}: use um número de {min} a {max:N0}.";
            f.Box.Focus();
            return false;
        }
        if (!Num(_fee, 0, 90, "Taxa", out long fee) || !Num(_target, 0, 10_000_000, "Meta líquida", out long target)
            || !Num(_small, 0, 10_000_000, "Limite de RAP para Small", out long small) || !Num(_reserve, 0, 20, "Reserva de Smalls", out long reserve)
            || !Num(_days, 0, 60, "Dias até a venda", out long days) || !Num(_undercut, 0, 1_000_000, "Abaixo do menor preço", out long undercut)
            || !Num(_floor, 0, 100, "Piso da sugestão", out long floor)) return;
        _s.FeePercent = (int)fee;
        _s.DefaultTargetNet = target;
        _s.SmallRapLimit = small;
        _s.MinSmallsReserve = (int)reserve;
        _s.DaysToSale = (int)days;
        _s.SellUndercut = (int)undercut;
        _s.SellFloorPercent = (int)floor;
        _s.PreferRolimonsValue = _rolimons.On;
        _s.UseSpaceAtSale = _atSale.On;
        _s.PreselectTradeItems = _preselect.On;
        _s.OpenDestinationAfterSend = _openDest.On;
        _s.SourceAccounts = _sources.Where(kv => kv.Value.On).Select(kv => kv.Key).ToList();
        _s.DestinationGroup = _dest.Selected is { } g && g != AllOthers ? g : null;
        // Exactly the group's accounts marked: follow the group (new accounts in it join); otherwise the chosen list.
        var marked = _dests.Where(kv => kv.Value.On).Select(kv => kv.Key).ToHashSet();
        var group = _dests.Keys.Where(InGroup).ToHashSet();
        _s.DestinationAccounts = marked.SetEquals(group) ? [] : [.. marked];
        if (marked.Count == 0) { _error.Text = "Marque ao menos uma destinatária."; return; }
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>In the group chosen in "Recebem os lotes" (every non-source account for "Todas as outras contas").</summary>
    private bool InGroup(long id) =>
        !(_sources.TryGetValue(id, out var src) && src.On)
        && (_dest.Selected is not { } g || g == AllOthers || string.Equals(_groupOf.GetValueOrDefault(id), g, StringComparison.Ordinal));

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }
}
