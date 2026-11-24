using RbxSwitcher.App.Platform;

namespace RbxSwitcher.App.UI;

/// <summary>Small dark dialogs: one or two text fields, OK/Cancel.</summary>
internal sealed class PromptDialog : Form
{
    private readonly TextBox _first;
    private readonly TextBox? _second;
    private readonly Label _error;
    private readonly Func<string, string?, string?>? _validate;

    public string Value => _first.Text;

    private PromptDialog(string title, string label, string initial, bool password, string? confirmLabel,
                         Func<string, string?, string?>? validate, string? note)
    {
        _validate = validate;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        if (note is not null)
            layout.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Theme.TextDim, Margin = new Padding(0, 0, 0, 8) });
        layout.Controls.Add(new Label { Text = label, AutoSize = true });
        _first = Theme.TextBox("");
        _first.Width = 360;
        _first.Text = initial;
        _first.UseSystemPasswordChar = password;
        layout.Controls.Add(_first);
        if (confirmLabel is not null)
        {
            layout.Controls.Add(new Label { Text = confirmLabel, AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
            _second = Theme.TextBox("");
            _second.Width = 360;
            _second.UseSystemPasswordChar = password;
            layout.Controls.Add(_second);
        }
        _error = new Label { AutoSize = true, ForeColor = Theme.Expired, MaximumSize = new Size(380, 0) };
        layout.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = 360, Margin = new Padding(0, 6, 0, 0) };
        var ok = Theme.Button("OK", (_, _) => TryAccept(), primary: true);
        var cancel = Theme.Button("Cancelar", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }

    private void TryAccept()
    {
        string? problem = _validate?.Invoke(_first.Text, _second?.Text);
        if (problem is not null)
        {
            _error.Text = problem;
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    public static string? Ask(IWin32Window? owner, string title, string label, string initial = "", string? note = null)
    {
        using var d = new PromptDialog(title, label, initial, false, null, null, note);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
    }

    public static string? AskPassword(IWin32Window? owner, string title, string label, string? note = null,
                                      Func<string, string?>? validate = null)
    {
        using var d = new PromptDialog(title, label, "", true, null,
            validate is null ? null : (a, _) => validate(a), note);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
    }

    /// <summary>New password + confirmation, at least 8 characters.</summary>
    public static string? AskNewPassword(IWin32Window? owner, string title, string? note = null)
    {
        using var d = new PromptDialog(title, "Nova senha mestra", "", true, "Repita a senha", (a, b) =>
            a.Length < 8 ? "Use pelo menos 8 caracteres." :
            a != b ? "As senhas não são iguais." : null, note);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
    }
}
