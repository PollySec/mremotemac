using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MRemoteMac.Converters;
using MRemoteMac.Rdp;

namespace MRemoteMac.Dialogs;

/// <summary>Code-built modal dialogs. Buttons follow macOS order: the default (primary) action is rightmost.</summary>
public static class Dialogs
{
    private static Window Frame(string title, Control body, params Button[] buttons)
    {
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons.OrderBy(b => b.IsDefault ? 1 : 0))
        {
            b.Classes.Add("dialog");
            if (b.IsDefault) b.Classes.Add("primary");
            buttonRow.Children.Add(b);
        }
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20, 16, 20, 16), MinWidth = 380, Children = { body, buttonRow } },
            Icon = AppIcon.Window
        };
        Services.TestMode.Prepare(window);
        return window;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 6) };

    private static DockPanel Labeled(string label, Control c)
        => new DockPanel { Margin = new Thickness(0, 2), Children = { new TextBlock { Text = label, Width = 84, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left }, c } };

    public static async Task<CertificateDecision> Certificate(Window owner, CertificateInfo c, bool allowAccept = true)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Heading(c.Changed
            ? $"The certificate for {c.Host}:{c.Port} has changed since it was last accepted. This may indicate a man-in-the-middle attack."
            : $"The identity of {c.Host}:{c.Port} could not be verified."));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        void Row(int r, string k, string v)
        {
            var kt = new TextBlock { Text = k, Margin = new Thickness(0, 2, 12, 2), Foreground = Brushes.Gray };
            var vt = new SelectableTextBlock { Text = v, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
            Grid.SetRow(kt, r); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
            grid.Children.Add(kt); grid.Children.Add(vt);
        }
        Row(0, "Common name", c.CommonName);
        Row(1, "Subject", c.Subject);
        Row(2, "Issuer", c.Issuer);
        Row(3, "Fingerprint", c.Fingerprint);
        body.Children.Add(grid);
        if (c.Changed)
            body.Children.Add(new TextBlock { Text = $"Previous fingerprint: {c.OldFingerprint}", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        if (!allowAccept)
            body.Children.Add(new TextBlock { Text = "This connection requires a trusted certificate (Authentication Level: AuthRequired).", Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });

        var result = CertificateDecision.Reject;
        var permanent = new Button { Content = "Accept permanently", IsEnabled = allowAccept };
        var once = new Button { Content = "Accept once", IsEnabled = allowAccept, IsDefault = allowAccept };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = !allowAccept };
        var w = Frame("Certificate verification", body, cancel, permanent, once);
        permanent.Click += (_, _) => { result = CertificateDecision.AcceptPermanently; w.Close(); };
        once.Click += (_, _) => { result = CertificateDecision.AcceptOnce; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    public static async Task<Credentials?> CredentialsPrompt(Window owner, AuthRequest req, string connectionName)
    {
        var user = new TextBox { Text = req.Username, Watermark = "Username", Width = 300 };
        var pass = new TextBox { Text = req.Password, Watermark = "Password", PasswordChar = '●', Width = 300 };
        var domain = new TextBox { Text = req.Domain, Watermark = "Domain (optional)", Width = 300 };
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Heading($"Enter credentials for {connectionName} ({req.ReasonText})"));
        body.Children.Add(Labeled("Username:", user));
        body.Children.Add(Labeled("Password:", pass));
        body.Children.Add(Labeled("Domain:", domain));
        Credentials? result = null;
        var ok = new Button { Content = "Connect", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Frame("Credentials", body, cancel, ok);
        ok.Click += (_, _) => { result = new Credentials(user.Text ?? "", pass.Text ?? "", domain.Text ?? ""); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => (string.IsNullOrEmpty(user.Text) ? user : pass).Focus();
        await w.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    public static async Task<string?> Password(Window owner, string prompt, string title = "Password", bool confirm = false)
    {
        var pass = new TextBox { PasswordChar = '●', Width = 300 };
        var pass2 = new TextBox { PasswordChar = '●', Width = 300 };
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Heading(prompt));
        body.Children.Add(Labeled("Password:", pass));
        if (confirm) body.Children.Add(Labeled("Verify:", pass2));
        var error = new TextBlock { Foreground = Brushes.DarkRed, IsVisible = false };
        body.Children.Add(error);
        string? result = null;
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Frame(title, body, cancel, ok);
        ok.Click += (_, _) =>
        {
            if (confirm && pass.Text != pass2.Text) { error.Text = "Passwords do not match."; error.IsVisible = true; return; }
            result = pass.Text ?? ""; w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => pass.Focus();
        await w.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    public static async Task<string?> Input(Window owner, string title, string prompt, string initial = "")
    {
        var tb = new TextBox { Text = initial, Width = 340 };
        var body = new StackPanel { Spacing = 4, Children = { Heading(prompt), tb } };
        string? result = null;
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Frame(title, body, cancel, ok);
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    public enum MsgButtons { Ok, YesNo, YesNoCancel }
    public enum MsgResult { Ok, Yes, No, Cancel }

    public static async Task<MsgResult> Message(Window owner, string title, string text, MsgButtons buttons = MsgButtons.Ok, string? detail = null)
    {
        var body = new StackPanel { Spacing = 4, Children = { Heading(text) } };
        if (detail is not null) body.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Foreground = Brushes.Gray });
        var result = buttons == MsgButtons.Ok ? MsgResult.Ok : MsgResult.Cancel;
        var list = new List<Button>();
        Window? w = null;
        Button Mk(string label, MsgResult r, bool def = false, bool cancel = false)
        {
            var b = new Button { Content = label, IsDefault = def, IsCancel = cancel };
            b.Click += (_, _) => { result = r; w?.Close(); };
            return b;
        }
        switch (buttons)
        {
            case MsgButtons.Ok: list.Add(Mk("OK", MsgResult.Ok, true, true)); break;
            case MsgButtons.YesNo: list.Add(Mk("No", MsgResult.No, false, true)); list.Add(Mk("Yes", MsgResult.Yes, true)); break;
            case MsgButtons.YesNoCancel: list.Add(Mk("Cancel", MsgResult.Cancel, false, true)); list.Add(Mk("No", MsgResult.No)); list.Add(Mk("Yes", MsgResult.Yes, true)); break;
        }
        w = Frame(title, body, list.ToArray());
        await w.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    public static async Task About(Window owner)
    {
        var body = new StackPanel { Spacing = 6, Width = 440 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(new Image { Source = AppIcon.Bitmap, Width = 72, Height = 72 });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "mRemoteMac", FontSize = 22, FontWeight = FontWeight.Bold });
        titles.Children.Add(new TextBlock { Text = $"Version {typeof(Dialogs).Assembly.GetName().Version?.ToString(3)}", Foreground = Brushes.Gray });
        titles.Children.Add(new TextBlock { Text = $".NET {Environment.Version}  ·  FreeRDP {Native.Version()}  ·  Avalonia {typeof(Window).Assembly.GetName().Version?.ToString(3)}", Foreground = Brushes.Gray, FontSize = 11 });
        header.Children.Add(titles);
        body.Children.Add(header);
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = "A native Apple Silicon port of the mRemoteNG connection manager with embedded RDP sessions, built on Avalonia UI and FreeRDP." });
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Text = "mRemoteMac is free software under the GNU General Public License v2, like mRemoteNG. FreeRDP is licensed under the Apache License 2.0. Icons from mRemoteNG and the Visual Studio Image Library." });
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var w = Frame("About mRemoteMac", body, ok);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner).ConfigureAwait(true);
    }
}
