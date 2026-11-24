using RbxSwitcher.App.Platform;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.App.UI;

internal enum PlayerLaunchChoice { Cancel, Open, OpenAndQuickLogin, SignInAutomatic }

/// <summary>
/// Replaces the browser's "Open Roblox?" prompt. Up front: what opens, the selected account, one short
/// notice and the main action. Other ways to open and the technical explanation (sign-in in the link,
/// program Windows starts) are collapsed. Nothing happens without a click.
/// </summary>
internal sealed class PlayerLaunchDialog : Form
{
    private int Width_ => LogicalToDeviceUnits(440);
    public PlayerLaunchChoice Choice { get; private set; } = PlayerLaunchChoice.Cancel;

    private PlayerLaunchDialog(string? account, PlayerLink link, ProtocolHandlers.Handler handler)
    {
        string purpose = PlayerLinks.Purpose(link);
        Text = purpose;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 16);

        bool installed = handler.Registered && handler.Exists;
        bool offerSignIn = !link.CarriesSignIn && account is not null;
        bool continues = offerSignIn && PlayerContinuation.CanContinue(link);
        bool transfer = link.Action.Contains("currency_transfer", StringComparison.OrdinalIgnoreCase);

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        Label L(string t, Color c, Font? f = null, int bottom = 8) =>
            new() { Text = t, AutoSize = true, MaximumSize = new Size(Width_, 0), ForeColor = c, Font = f ?? Theme.Base, Margin = new Padding(0, 0, 0, bottom) };

        // ── What opens, and for whom ──
        body.Controls.Add(L(purpose, Theme.Text, Theme.Headline, 12));
        if (account is not null)
        {
            var who = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            who.Controls.Add(new Label { Text = "Conta selecionada", AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.Small, Margin = new Padding(0, 2, 8, 0) });
            who.Controls.Add(new Label { Text = "@" + account, AutoSize = true, ForeColor = Theme.Text, Font = Theme.Bold, Margin = Padding.Empty });
            body.Controls.Add(who);
        }

        // ── One short notice ──
        string notice = !installed
            ? "O Roblox não parece instalado nesta conta do Windows. Instale pelo site oficial: roblox.com/download."
            : offerSignIn
                ? "O Roblox será aberto com essa conta. Se o aplicativo já estiver aberto em outra conta, feche-o antes de continuar."
                : link.CarriesSignIn
                    ? $"O site já enviou o login{(account is null ? "" : $" de @{account}")} neste link; o Roblox deve abrir nessa conta."
                    : "Esta aba não está ligada a uma conta salva. O Roblox abre na conta que já estiver logada no aplicativo.";
        body.Controls.Add(new NoteLabel { Text = notice, Kind = installed ? NoteKind.Info : NoteKind.Bad, MaximumSize = new Size(Width_, 0), Margin = new Padding(0, 0, 0, 8) });
        body.Controls.Add(L(transfer ? "Confira o nome da conta no Roblox antes de enviar." : "Confira o nome da conta no Roblox antes de continuar.",
            Theme.WarnInk, Theme.Small, 14));

        // ── Main action + cancel ──
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        Button primary = offerSignIn
            ? Theme.Button(continues ? $"Entrar como @{account} e abrir" : $"Entrar como @{account}", (_, _) => Done(PlayerLaunchChoice.SignInAutomatic), primary: true)
            : Theme.Button("Abrir o Roblox", (_, _) => Done(PlayerLaunchChoice.Open), primary: true);
        primary.Enabled = handler.Registered;
        primary.Margin = new Padding(0, 0, 8, 0);
        var cancel = Theme.Button("Cancelar", (_, _) => Done(PlayerLaunchChoice.Cancel));
        cancel.Margin = Padding.Empty;
        buttons.Controls.AddRange([primary, cancel]);
        body.Controls.Add(buttons);

        // ── Mais opções (collapsed): the other ways to open, each with one line of what it does ──
        if (offerSignIn)
        {
            var options = Section();
            options.Controls.Add(Option("Só abrir o Roblox", "Usa a conta que já está logada no aplicativo.", PlayerLaunchChoice.Open));
            options.Controls.Add(Option("Entrar pelo Quick Login", "Abre o Roblox e mostra aqui a página para confirmar o código de login.", PlayerLaunchChoice.OpenAndQuickLogin));
            body.Controls.Add(Toggle("Mais opções", options));
            body.Controls.Add(options);
        }

        // ── Detalhes técnicos (collapsed) ──
        var details = Section();
        details.Controls.Add(L($"Pedido do site: {link.Action}.", Theme.Text2, Theme.Small));
        details.Controls.Add(L(link.CarriesSignIn
            ? "O link traz um login de uso único gerado pelo próprio roblox.com nesta aba."
            : "O link não traz login: o aplicativo Roblox usa a conta em que ele mesmo está logado, não a do RbxSwitcher nem a de um navegador." +
              (offerSignIn ? " \"Entrar como\" usa o mesmo login de uso único do botão \"Jogar\" do site" +
                             (continues ? " e, quando o aplicativo abre, pede de novo a tela que o site pediu." : ".") : ""),
            Theme.Text2, Theme.Small));
        details.Controls.Add(L($"Programa que o Windows abre: {ProtocolHandlers.Text(handler)}", Theme.TextDim, Theme.Small));
        details.Controls.Add(L("O RbxSwitcher não envia Robux e não confirma nada no aplicativo: o envio é feito só por você.", Theme.TextDim, Theme.Small, 0));
        body.Controls.Add(Toggle("Detalhes técnicos", details));
        body.Controls.Add(details);

        Controls.Add(body);
        AcceptButton = primary;
        CancelButton = cancel;

        FlowLayoutPanel Section() => new()
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false,
            Margin = new Padding(0, 0, 0, 4), Padding = new Padding(2, 2, 0, 4),
        };

        Control Option(string text, string description, PlayerLaunchChoice choice)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            var b = Theme.Button(text, (_, _) => Done(choice));
            b.Enabled = handler.Registered;
            b.Margin = Padding.Empty;
            row.Controls.Add(b);
            row.Controls.Add(L(description, Theme.TextDim, Theme.Small, 0));
            return row;
        }

        Control Toggle(string text, Control section)
        {
            var t = new UiButton { Text = text, Kind = ButtonKind.Ghost, Icon = UiIcon.ChevronDown, IconAfterText = true, Margin = new Padding(0, 8, 0, 0) };
            t.Click += (_, _) =>
            {
                section.Visible = !section.Visible;
                t.Icon = section.Visible ? UiIcon.ChevronUp : UiIcon.ChevronDown;
                t.AccessibleName = text + (section.Visible ? ", aberto" : ", fechado");
                t.Invalidate();
                // Opening a section can push the bottom off the screen: move the dialog up to keep it all visible.
                BeginInvoke(() =>
                {
                    var area = Screen.FromControl(this).WorkingArea;
                    if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
                });
            };
            return t;
        }
    }

    private void Done(PlayerLaunchChoice c)
    {
        Choice = c;
        DialogResult = c == PlayerLaunchChoice.Cancel ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }

    public static PlayerLaunchChoice Ask(IWin32Window owner, string? account, PlayerLink link, ProtocolHandlers.Handler handler)
    {
        using var d = new PlayerLaunchDialog(account, link, handler);
        d.ShowDialog(owner);
        return d.Choice;
    }
}
