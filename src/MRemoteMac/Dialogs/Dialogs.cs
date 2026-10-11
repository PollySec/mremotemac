using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MRemoteMac.Rdp;

namespace MRemoteMac.Dialogs;

/// <summary>Small code-built dialogs (certificate, credentials, master password, text input, message box, about).</summary>
public static class Dialogs
{
    private static Window Frame(string title, Control body, params Button[] buttons)
    {
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) { b.MinWidth = 80; buttonRow.Children.Add(b); }
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(16), MinWidth = 380, Children = { body, buttonRow } },
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://mRemoteMac/Assets/Images/mRemoteNG_Icon.png")))
        };
        return w;
    }

    public static async Task<CertificateDecision> Certificate(Window owner, CertificateInfo c, bool allowAccept = true)
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            Text = c.Changed
                ? $"The certificate for {c.Host}:{c.Port} has CHANGED since it was last accepted. This may indicate a man-in-the-middle attack."
                : $"The certificate for {c.Host}:{c.Port} could not be verified.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 460, FontWeight = FontWeight.Bold
        });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        void Row(int r, string k, string v)
        {
            var kt = new TextBlock { Text = k, Margin = new Thickness(0, 2, 12, 2), Foreground = Brushes.Gray };
            var vt = new TextBlock { Text = v, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
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
        var w = Frame("Certificate verification", body, permanent, once, cancel);
        permanent.Click += (_, _) => { result = CertificateDecision.AcceptPermanently; w.Close(); };
        once.Click += (_, _) => { result = CertificateDecision.AcceptOnce; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<Credentials?> CredentialsPrompt(Window owner, AuthRequest req, string connectionName)
    {
        var user = new TextBox { Text = req.Username, Watermark = "Username", Width = 300 };
        var pass = new TextBox { Text = req.Password, Watermark = "Password", PasswordChar = '●', Width = 300 };
        var domain = new TextBox { Text = req.Domain, Watermark = "Domain", Width = 300 };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = $"Enter credentials for {connectionName} ({req.ReasonText})", FontWeight = FontWeight.Bold });
        body.Children.Add(Labeled("Username:", user));
        body.Children.Add(Labeled("Password:", pass));
        body.Children.Add(Labeled("Domain:", domain));
        Credentials? result = null;
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Frame("Credentials", body, ok, cancel);
        ok.Click += (_, _) => { result = new Credentials(user.Text ?? "", pass.Text ?? "", domain.Text ?? ""); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => (string.IsNullOrEmpty(user.Text) ? user : pass).Focus();
        await w.ShowDialog(owner);
        return result;
    }

    private static Control Labeled(string label, Control c)
        => new DockPanel { Children = { new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left }, c } };

    public static async Task<string?> Password(Window owner, string prompt, string title = "Password", bool confirm = false)
    {
        var pass = new TextBox { PasswordChar = '●', Width = 300 };
        var pass2 = new TextBox { PasswordChar = '●', Width = 300 };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 });
        body.Children.Add(Labeled("Password:", pass));
        if (confirm) body.Children.Add(Labeled("Verify:", pass2));
        var error = new TextBlock { Foreground = Brushes.DarkRed, IsVisible = false };
        body.Children.Add(error);
        string? result = null;
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Frame(title, body, ok, cancel);
        ok.Click += (_, _) =>
        {
            if (confirm && pass.Text != pass2.Text) { error.Text = "Passwords do not match."; error.IsVisible = true; return; }
            result = pass.Text ?? ""; w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => pass.Focus();
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> Input(Window owner, string title, string prompt, string initial = "")
    {
        var tb = new TextBox { Text = initial, Width = 340 };
        var body = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = prompt }, tb } };
        string? result = null;
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Frame(title, body, ok, cancel);
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    public enum MsgButtons { Ok, YesNo, YesNoCancel }
    public enum MsgResult { Ok, Yes, No, Cancel }

    public static async Task<MsgResult> Message(Window owner, string title, string text, MsgButtons buttons = MsgButtons.Ok)
    {
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
        var result = buttons == MsgButtons.Ok ? MsgResult.Ok : MsgResult.Cancel;
        var list = new List<Button>();
        Window? w = null;
        Button Mk(string text, MsgResult r, bool def = false, bool cancel = false)
        {
            var b = new Button { Content = text, IsDefault = def, IsCancel = cancel };
            b.Click += (_, _) => { result = r; w?.Close(); };
            return b;
        }
        switch (buttons)
        {
            case MsgButtons.Ok: list.Add(Mk("OK", MsgResult.Ok, true, true)); break;
            case MsgButtons.YesNo: list.Add(Mk("Yes", MsgResult.Yes, true)); list.Add(Mk("No", MsgResult.No, false, true)); break;
            case MsgButtons.YesNoCancel: list.Add(Mk("Yes", MsgResult.Yes, true)); list.Add(Mk("No", MsgResult.No)); list.Add(Mk("Cancel", MsgResult.Cancel, false, true)); break;
        }
        w = Frame(title, body, list.ToArray());
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task About(Window owner)
    {
        var body = new StackPanel { Spacing = 8, Width = 420 };
        body.Children.Add(new Image { Source = Converters.ImageCache.Get("avares://mRemoteMac/Assets/Images/Header_dark.png"), Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left });
        body.Children.Add(new TextBlock { Text = "mRemoteMac", FontSize = 20, FontWeight = FontWeight.Bold });
        body.Children.Add(new TextBlock { Text = $"Version {typeof(Dialogs).Assembly.GetName().Version?.ToString(3)}  ·  .NET {Environment.Version}  ·  FreeRDP {Native.Version()}", Foreground = Brushes.Gray });
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "A macOS (Apple Silicon) port of the mRemoteNG connection manager, RDP only. Built with Avalonia UI and FreeRDP." });
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "mRemoteMac is free software licensed under the GNU General Public License v2, like mRemoteNG. FreeRDP is licensed under the Apache License 2.0. Icons from mRemoteNG and the Visual Studio Image Library.", Foreground = Brushes.Gray, FontSize = 11 });
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var w = Frame("About mRemoteMac", body, ok);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }
}
